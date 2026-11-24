// -----------------------------------------------------------------------------
//  UCMobileImportPolicy
//
//  Applies the import settings a static foliage mesh needs to behave on a
//  flagship phone, and cross fades the LOD transitions so the reduced tiers do
//  not pop.
//
//  WHY THIS IS SEPARATE FROM UCAssetMaintenance
//  --------------------------------------------
//  UCAssetMaintenance migrates a meta into the current layout and RESTORES every
//  setting it found, verifying the guid never moved. That is a different job from
//  deciding what the settings ought to be, and merging them would make the
//  migration tool unable to prove it changed nothing. This file decides policy;
//  that file preserves identity. Running this one is also what makes the editor
//  rewrite each meta in the current format with today's timestamp, so the two
//  together cover the whole meta concern without either duplicating the other.
//
//  WHAT EACH SETTING ACTUALLY BUYS, AND WHAT IT DOES NOT
//  ----------------------------------------------------
//  meshCompression   - shrinks the mesh on DISK and in the built player. It does
//                      NOT reduce runtime RAM: Unity decompresses into full
//                      vertex buffers on load. It is the right lever for the
//                      project size complaint and the wrong one for RAM.
//  meshOptimization  - reorders vertices and indices for GPU cache coherency.
//                      Free frame time, no visual change, no size change.
//  blendshapes,      - data a static tree cannot use. Removing it costs nothing
//  cameras, lights,    and shrinks both the asset and the import time.
//  animation
//
//  RAM is reduced by vertex count, which is the decimation pass's job, not this
//  one's. isReadable is already 0 across the corpus, so there is no second CPU
//  copy to remove.
//
//  DELIBERATELY NOT TOUCHED
//  ------------------------
//  importTangents. Dropping tangents saves four floats per vertex, but any
//  material with a normal map needs them and shades wrongly without them. That
//  depends on the materials, not the mesh, so it is reported rather than changed.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor.Diagnostics
{
    /// <summary>
    /// Sets mobile-appropriate model import settings across the package content and
    /// cross-fades LOD transitions. Editor-only.
    /// </summary>
    public static class UCMobileImportPolicy
    {
        private const string ContentFolder = "Assets/UniverseCreator/Content";

        // Medium, not High. Compression quantises positions, normals and UVs; on
        // alpha-cut foliage the UV quantisation of High can walk a leaf's edge off
        // its texture island and show as a clipped leaf. Medium is the strongest
        // setting that has no visible cost on cards.
        private const ModelImporterMeshCompression Compression =
            ModelImporterMeshCompression.Medium;

        [MenuItem("Tools/Universe Creator/Mobile/Apply Model Import Policy", false, 950)]
        public static void RunMenu()
        {
            bool ok = Apply(false, out string report);
            Debug.Log(report);
            if (!ok) Debug.LogError("Universe Creator: model import policy did not fully apply.");
        }

        [MenuItem("Tools/Universe Creator/Mobile/Apply Model Import Policy (dry run)", false, 951)]
        public static void RunMenuDryRun()
        {
            Apply(true, out string report);
            Debug.Log(report);
        }

        /// <summary>Headless dry run. Writes nothing.</summary>
        public static void RunBatchDryRun()
        {
            Apply(true, out string report);
            Debug.Log(report);
            Console.WriteLine(report);
            EditorApplication.Exit(0);
        }

        /// <summary>Headless apply. Non-zero exit if any importer failed to save.</summary>
        public static void RunBatch()
        {
            bool ok = Apply(false, out string report);
            Debug.Log(report);

            // Console.WriteLine as well as Debug.Log: in batch the log file is the
            // only artefact, and this keeps the verdict greppable above import spam.
            Console.WriteLine(report);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// Applies the policy to every model under Content. dryRun reports the
        /// changes it would make and writes nothing.
        /// </summary>
        public static bool Apply(bool dryRun, out string report)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== UC MOBILE MODEL IMPORT POLICY" + (dryRun ? " (DRY RUN)" : "") + " ===");

            string[] guids = AssetDatabase.FindAssets("t:GameObject", new[] { ContentFolder });

            // Distinct asset paths, because one model yields many sub-objects and
            // FindAssets reports the root per hit.
            List<string> models = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                if (AssetImporter.GetAtPath(path) is ModelImporter) models.Add(path);
            }
            models.Sort();
            sb.AppendLine("models found   : " + models.Count);

            int changed = 0;
            int already = 0;
            int failed = 0;
            List<string> tangentNote = new List<string>();

            try
            {
                if (!dryRun) AssetDatabase.StartAssetEditing();

                for (int i = 0; i < models.Count; i++)
                {
                    string path = models[i];
                    ModelImporter mi = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (mi == null) continue;

                    List<string> diff = new List<string>();

                    if (mi.meshCompression != Compression)
                        diff.Add("meshCompression " + mi.meshCompression + "->" + Compression);
                    if (mi.meshOptimizationFlags != MeshOptimizationFlags.Everything)
                        diff.Add("meshOptimization " + mi.meshOptimizationFlags + "->Everything");
                    if (mi.importBlendShapes) diff.Add("blendShapes off");
                    if (mi.importCameras) diff.Add("cameras off");
                    if (mi.importLights) diff.Add("lights off");
                    if (mi.importVisibility) diff.Add("visibility off");
                    if (mi.importAnimation) diff.Add("animation off");
                    if (mi.importConstraints) diff.Add("constraints off");
                    if (mi.animationType != ModelImporterAnimationType.None)
                        diff.Add("animationType " + mi.animationType + "->None");
                    if (!mi.weldVertices) diff.Add("weldVertices on");
                    if (mi.isReadable) diff.Add("isReadable off (drops the CPU copy)");

                    // Reported, never changed - see the header note. A model whose
                    // materials have no normal map could drop these, but that is a
                    // material question and guessing it shades the asset wrongly.
                    if (mi.importTangents != ModelImporterTangents.None)
                        tangentNote.Add(System.IO.Path.GetFileName(path) + " (" + mi.importTangents + ")");

                    if (diff.Count == 0) { already++; continue; }

                    sb.AppendLine((dryRun ? "would fix  " : "fixed      ") + path);
                    for (int d = 0; d < diff.Count; d++) sb.AppendLine("             " + diff[d]);

                    if (dryRun) { changed++; continue; }

                    mi.meshCompression = Compression;
                    mi.meshOptimizationFlags = MeshOptimizationFlags.Everything;
                    mi.importBlendShapes = false;
                    mi.importCameras = false;
                    mi.importLights = false;
                    mi.importVisibility = false;
                    mi.importAnimation = false;
                    mi.importConstraints = false;
                    mi.animationType = ModelImporterAnimationType.None;
                    mi.weldVertices = true;
                    mi.isReadable = false;

                    // SaveAndReimport is what makes the editor reserialize the meta,
                    // so the file lands in the current layout with today's timestamp.
                    // The guid is a property of the asset, not the importer, and is
                    // untouched by this - which is why every reference survives.
                    try
                    {
                        EditorUtility.SetDirty(mi);
                        mi.SaveAndReimport();
                        changed++;
                    }
                    catch (Exception e)
                    {
                        sb.AppendLine("FAILED     " + path + "  " + e.Message);
                        failed++;
                    }
                }
            }
            finally
            {
                if (!dryRun)
                {
                    AssetDatabase.StopAssetEditing();
                    AssetDatabase.Refresh();
                }
            }

            sb.AppendLine();
            sb.AppendLine("changed        : " + changed);
            sb.AppendLine("already correct: " + already);
            sb.AppendLine("failed         : " + failed);

            if (tangentNote.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("ADVISORY - tangents still imported on " + tangentNote.Count
                              + " model(s). Four floats per vertex each. Safe to drop only on");
                sb.AppendLine("models whose materials use no normal map; not changed here because");
                sb.AppendLine("that is a material decision and guessing it shades the asset wrongly.");
            }

            sb.AppendLine();
            sb.AppendLine(failed == 0 ? "RESULT: OK" : "RESULT: FAILURES PRESENT");
            report = sb.ToString();
            return failed == 0;
        }

        // ------------------------------------------------------------ cross fade --

        [MenuItem("Tools/Universe Creator/Mobile/Cross-Fade LOD Transitions", false, 955)]
        public static void RunCrossFadeMenu()
        {
            bool ok = CrossFade(false, out string report);
            Debug.Log(report);
            if (!ok) Debug.LogError("Universe Creator: LOD cross-fade did not fully apply.");
        }

        /// <summary>Headless. Non-zero exit if a prefab could not be saved.</summary>
        public static void RunBatchCrossFade()
        {
            bool ok = CrossFade(false, out string report);
            Debug.Log(report);
            Console.WriteLine(report);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// Turns on dithered LOD cross-fading for every LODGroup in the package.
        ///
        /// This is the correct compensation for reducing LOD1 and below while LOD0
        /// is left at full density: the first transition now spans a larger triangle
        /// drop than the vendor authored, and an abrupt swap across a larger drop is
        /// what reads as a pop. Cross-fading dissolves it across a screen-space band
        /// instead, which costs a few overlapping pixels during the transition only.
        ///
        /// animateCrossFading stays false, so the blend is dithered rather than
        /// timed. Dithered is the cheaper of the two on mobile and is what foliage
        /// normally uses, because a timed fade on an instance the camera passes
        /// quickly can still be mid-blend when it leaves the band.
        ///
        /// NOTE: dithered cross-fade needs the material's shader to handle the
        /// LOD_FADE_CROSSFADE keyword. A shader that ignores it will not fade - it
        /// will simply pop as before, which is a no-op rather than a corruption. The
        /// report lists which shaders were seen so that can be checked.
        /// </summary>
        public static bool CrossFade(bool dryRun, out string report)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== UC LOD CROSS-FADE" + (dryRun ? " (DRY RUN)" : "") + " ===");

            string[] guids = AssetDatabase.FindAssets("t:GameObject", new[] { ContentFolder });
            int groups = 0;
            int fixedUp = 0;
            int failed = 0;
            HashSet<string> shaders = new HashSet<string>();
            HashSet<string> seen = new HashSet<string>();

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;

                // Only a prefab can be written back. An FBX's LODGroup, if it had
                // one, lives in immutable imported data.
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;

                GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null) continue;

                LODGroup[] found = root.GetComponentsInChildren<LODGroup>(true);
                if (found.Length == 0) continue;

                bool dirty = false;
                for (int g = 0; g < found.Length; g++)
                {
                    groups++;

                    Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
                    for (int r = 0; r < rs.Length; r++)
                    {
                        Material[] mats = rs[r].sharedMaterials;
                        for (int m = 0; m < mats.Length; m++)
                            if (mats[m] != null && mats[m].shader != null)
                                shaders.Add(mats[m].shader.name);
                    }

                    if (found[g].fadeMode == LODFadeMode.CrossFade
                        && !found[g].animateCrossFading) continue;

                    sb.AppendLine((dryRun ? "would fade " : "faded      ") + path
                                  + "  " + found[g].fadeMode + "->CrossFade (dithered)");
                    if (dryRun) { fixedUp++; continue; }

                    found[g].fadeMode = LODFadeMode.CrossFade;
                    found[g].animateCrossFading = false;
                    dirty = true;
                    fixedUp++;
                }

                if (!dirty) continue;
                try
                {
                    EditorUtility.SetDirty(root);
                    PrefabUtility.SavePrefabAsset(root);
                }
                catch (Exception e)
                {
                    sb.AppendLine("FAILED     " + path + "  " + e.Message);
                    failed++;
                }
            }

            if (!dryRun) AssetDatabase.SaveAssets();

            sb.AppendLine();
            sb.AppendLine("LODGroups seen : " + groups);
            sb.AppendLine("cross-faded    : " + fixedUp);
            sb.AppendLine("failed         : " + failed);

            if (shaders.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("shaders on these renderers - each must handle LOD_FADE_CROSSFADE");
                sb.AppendLine("for the fade to be visible; one that ignores it just pops as before:");
                foreach (string s in shaders) sb.AppendLine("  " + s);
            }

            sb.AppendLine();
            sb.AppendLine(failed == 0 ? "RESULT: OK" : "RESULT: FAILURES PRESENT");
            report = sb.ToString();
            return failed == 0;
        }
    }
}
