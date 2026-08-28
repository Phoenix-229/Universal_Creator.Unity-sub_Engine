// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCPrefabIntegrity.cs
//  Purpose: Reads every prefab under Content and reports structural defects that
//           Unity only complains about later, at runtime, in someone's scene.
//
//  WHY THIS EXISTS
//  ---------------
//  A decimated tree prefab shipped with a null entry in its LODGroup LOD0
//  renderer list. Unity logs "LODGroup has an LOD level with no renderers" style
//  warnings for that, and the person running the scene sees it - but the self
//  test never looked at a prefab at all, so nothing upstream could have caught
//  it. Every defect listed here is one that survives a clean compile, passes a
//  texture and shader audit, and only shows itself once the asset is on screen.
//
//  DEFECT vs ADVISORY
//  ------------------
//  A defect is something Unity itself will complain about, or that guarantees a
//  missing draw: a null renderer, an LOD level with nothing in it, LOD screen
//  heights that do not descend (levels after the break are unreachable), a
//  MeshFilter with no mesh. Those fail the self test.
//
//  An advisory is a shape worth knowing about that is still legal: a renderer
//  with more or fewer material slots than the mesh has submeshes, an empty
//  material slot, two collider kinds on one prefab. Vendor content arrives like
//  that legitimately, and the package's own rule is that a prefab with no
//  matching material must still work - so these are reported, never failed on.
//
//  The fix pass only ever removes null renderer entries, and it does that through
//  PrefabUtility so the editor serializes the result. Nothing here edits prefab
//  YAML by hand: raising or rewriting serialized text behind the editor's back is
//  how a prefab ends up readable but wrong.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor.Diagnostics
{
    /// <summary>
    /// Prefab structure audit for the shipped content. Editor-only, read-only
    /// unless FixNullRenderers is called explicitly.
    /// </summary>
    public static class UCPrefabIntegrity
    {
        public const string ContentRoot = "Assets/UniverseCreator/Content";

        // t:GameObject rather than t:Prefab, deliberately. The package ships one
        // real .prefab and 48 imported FBX models, and the decimation risk lives in
        // the models - so a filter that only saw .prefab files would audit 1 asset
        // out of 49 and report PASS.
        private const string AssetFilter = "t:GameObject";

        public enum Kind
        {
            NullRenderer,
            EmptyLodLevel,
            LodHeightNotDescending,
            MissingMesh,
            MissingLodGroupMesh,
            MaterialCountMismatch,
            EmptyMaterialSlot,
            MixedColliders
        }

        /// <summary>One problem found on one prefab.</summary>
        public struct Finding
        {
            public string AssetPath;
            public string Detail;
            public Kind Kind;

            public override string ToString()
            {
                return Kind + "  " + AssetPath + "\n        " + Detail;
            }
        }

        /// <summary>
        /// True when Unity itself would complain, or a draw is guaranteed missing.
        /// Only these fail the self test.
        /// </summary>
        public static bool IsDefect(Kind kind)
        {
            return kind == Kind.NullRenderer
                || kind == Kind.EmptyLodLevel
                || kind == Kind.LodHeightNotDescending
                || kind == Kind.MissingMesh
                || kind == Kind.MissingLodGroupMesh;
        }

        // ---------------------------------------------------------------- scan --

        /// <summary>
        /// Reads every prefab under Content. Returns one entry per problem, and
        /// sets scanned to the number of prefabs actually opened.
        /// </summary>
        public static List<Finding> Scan(out int scanned)
        {
            List<Finding> found = new List<Finding>();
            scanned = 0;

            string[] guids = AssetDatabase.FindAssets(AssetFilter, new[] { ContentRoot });
            for (int g = 0; g < guids.Length; g++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[g]);
                GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null) continue;

                scanned++;
                InspectLodGroups(root, path, found);
                InspectRenderers(root, path, found);
                InspectColliders(root, path, found);
            }

            return found;
        }

        private static void InspectLodGroups(GameObject root, string path, List<Finding> found)
        {
            LODGroup[] groups = root.GetComponentsInChildren<LODGroup>(true);
            for (int i = 0; i < groups.Length; i++)
            {
                LOD[] lods = groups[i].GetLODs();
                string who = groups.Length > 1 ? " on " + groups[i].name : string.Empty;

                for (int level = 0; level < lods.Length; level++)
                {
                    Renderer[] renderers = lods[level].renderers;

                    if (renderers == null || renderers.Length == 0)
                    {
                        found.Add(new Finding
                        {
                            AssetPath = path,
                            Kind = Kind.EmptyLodLevel,
                            Detail = "LOD" + level + who + " lists no renderers, so nothing draws in " +
                                     "that distance band."
                        });
                        continue;
                    }

                    int nulls = 0;
                    for (int r = 0; r < renderers.Length; r++)
                    {
                        if (renderers[r] == null) nulls++;
                    }

                    if (nulls > 0)
                    {
                        found.Add(new Finding
                        {
                            AssetPath = path,
                            Kind = Kind.NullRenderer,
                            Detail = "LOD" + level + who + " declares " + renderers.Length +
                                     " renderer slot(s) and " + nulls + " of them are empty. Unity " +
                                     "warns about this every time the LODGroup is culled. Run " +
                                     "Tools/Universe Creator/Diagnostics/Fix Prefab LOD Renderers."
                        });
                    }

                    // A renderer inside an LOD with no mesh behind it draws nothing,
                    // which is the same visible symptom as the null slot above but a
                    // different cause, so it is named separately.
                    for (int r = 0; r < renderers.Length; r++)
                    {
                        MeshRenderer mr = renderers[r] as MeshRenderer;
                        if (mr == null) continue;

                        MeshFilter filter = mr.GetComponent<MeshFilter>();
                        if (filter != null && filter.sharedMesh == null)
                        {
                            found.Add(new Finding
                            {
                                AssetPath = path,
                                Kind = Kind.MissingLodGroupMesh,
                                Detail = "LOD" + level + who + " points at " + mr.name +
                                         ", whose MeshFilter has no mesh."
                            });
                        }
                    }
                }

                // Unity picks a level by walking these downward. A value that does
                // not descend makes every later level unreachable, so the cheap LODs
                // silently never run - the exact opposite of why they exist.
                for (int level = 1; level < lods.Length; level++)
                {
                    float prev = lods[level - 1].screenRelativeTransitionHeight;
                    float cur = lods[level].screenRelativeTransitionHeight;
                    if (cur < prev) continue;

                    found.Add(new Finding
                    {
                        AssetPath = path,
                        Kind = Kind.LodHeightNotDescending,
                        Detail = "LOD" + level + who + " transitions at " + cur.ToString("0.#####") +
                                 " which is not below LOD" + (level - 1) + " at " +
                                 prev.ToString("0.#####") + ". Levels from here down never run."
                    });
                }
            }
        }

        private static void InspectRenderers(GameObject root, string path, List<Finding> found)
        {
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                if (filters[i].sharedMesh != null) continue;

                found.Add(new Finding
                {
                    AssetPath = path,
                    Kind = Kind.MissingMesh,
                    Detail = filters[i].name + " has a MeshFilter with no mesh. Either the FBX " +
                             "sub-mesh it pointed at was renamed, or a decimation pass wrote a " +
                             "file whose object names no longer match."
                });
            }

            MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Material[] mats = renderers[i].sharedMaterials;

                int empty = 0;
                for (int m = 0; m < mats.Length; m++)
                {
                    if (mats[m] == null) empty++;
                }
                if (empty > 0)
                {
                    found.Add(new Finding
                    {
                        AssetPath = path,
                        Kind = Kind.EmptyMaterialSlot,
                        Detail = renderers[i].name + " has " + empty + " empty material slot(s) of " +
                                 mats.Length + ". It renders pink rather than failing."
                    });
                }

                MeshFilter filter = renderers[i].GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;

                int submeshes = filter.sharedMesh.subMeshCount;
                if (mats.Length == submeshes) continue;

                found.Add(new Finding
                {
                    AssetPath = path,
                    Kind = Kind.MaterialCountMismatch,
                    Detail = renderers[i].name + " carries " + mats.Length + " material(s) for " +
                             submeshes + " submesh(es)." +
                             (mats.Length < submeshes
                                 ? " Submeshes past the last material reuse it."
                                 : " The extra material(s) never draw.")
                });
            }
        }

        private static void InspectColliders(GameObject root, string path, List<Finding> found)
        {
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);

            int meshColliders = 0;
            int primitives = 0;
            StringBuilder names = new StringBuilder();

            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] is MeshCollider) meshColliders++;
                else primitives++;

                if (names.Length > 0) names.Append(", ");
                names.Append(colliders[i].GetType().Name).Append(" on ").Append(colliders[i].name);
            }

            if (meshColliders == 0 || primitives == 0) return;

            found.Add(new Finding
            {
                AssetPath = path,
                Kind = Kind.MixedColliders,
                Detail = "two collision shapes on one prefab (" + names +
                         "). A tree needs one. Which to keep is a design call, so this is " +
                         "reported rather than changed."
            });
        }

        // ----------------------------------------------------------------- fix --

        /// <summary>
        /// Rebuilds any LOD renderer array that contains a null entry, keeping the
        /// real renderers in order. Returns how many LOD levels were rewritten.
        ///
        /// The prefab is opened with LoadPrefabContents and saved with
        /// SaveAsPrefabAsset: that is the editor path, so Unity writes the file and
        /// its meta. Editing the asset object in place is not reliably persisted.
        /// </summary>
        public static int FixNullRenderers(bool dryRun, out string report)
        {
            StringBuilder log = new StringBuilder();
            int levelsFixed = 0;
            int prefabsTouched = 0;

            string[] guids = AssetDatabase.FindAssets(AssetFilter, new[] { ContentRoot });

            for (int g = 0; g < guids.Length; g++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[g]);

                // The scan filter also returns imported model roots (.fbx and
                // friends). Those are read-only: their hierarchy is rebuilt by the
                // importer on every reimport, and LoadPrefabContents on one throws.
                // Worth scanning, but they cannot be edited here.
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;

                // Cheap pre-check on the loaded asset so a clean prefab is never
                // opened for editing - opening every prefab under Content would be
                // slow and would dirty files that have nothing wrong with them.
                GameObject probe = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (probe == null || !HasNullRenderer(probe)) continue;

                if (dryRun)
                {
                    log.AppendLine("would fix  " + path);
                    prefabsTouched++;
                    continue;
                }

                GameObject contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    int here = StripNulls(contents, path, log);
                    if (here > 0)
                    {
                        levelsFixed += here;
                        prefabsTouched++;
                        PrefabUtility.SaveAsPrefabAsset(contents, path);
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }

            log.AppendLine("prefabs    : " + prefabsTouched);
            log.AppendLine("lod levels : " + levelsFixed);
            report = log.ToString();
            return levelsFixed;
        }

        private static bool HasNullRenderer(GameObject root)
        {
            LODGroup[] groups = root.GetComponentsInChildren<LODGroup>(true);
            for (int i = 0; i < groups.Length; i++)
            {
                LOD[] lods = groups[i].GetLODs();
                for (int level = 0; level < lods.Length; level++)
                {
                    Renderer[] renderers = lods[level].renderers;
                    if (renderers == null) continue;
                    for (int r = 0; r < renderers.Length; r++)
                    {
                        if (renderers[r] == null) return true;
                    }
                }
            }
            return false;
        }

        private static int StripNulls(GameObject root, string path, StringBuilder log)
        {
            int levels = 0;
            LODGroup[] groups = root.GetComponentsInChildren<LODGroup>(true);

            for (int i = 0; i < groups.Length; i++)
            {
                LOD[] lods = groups[i].GetLODs();
                bool changed = false;

                for (int level = 0; level < lods.Length; level++)
                {
                    Renderer[] renderers = lods[level].renderers;
                    if (renderers == null) continue;

                    List<Renderer> kept = new List<Renderer>(renderers.Length);
                    for (int r = 0; r < renderers.Length; r++)
                    {
                        if (renderers[r] != null) kept.Add(renderers[r]);
                    }

                    if (kept.Count == renderers.Length) continue;

                    // Refusing here matters: an LOD level emptied by this pass is a
                    // worse prefab than the one with the warning, because the tree
                    // would disappear at that distance instead of just logging.
                    if (kept.Count == 0)
                    {
                        log.AppendLine("SKIP       " + path + " LOD" + level +
                                       " is entirely null - emptying it would delete the level. " +
                                       "Needs the renderer reassigned by hand.");
                        continue;
                    }

                    log.AppendLine("fixed      " + path + " LOD" + level + "  " +
                                   renderers.Length + " slots -> " + kept.Count);
                    lods[level].renderers = kept.ToArray();
                    changed = true;
                    levels++;
                }

                if (changed) groups[i].SetLODs(lods);
            }

            return levels;
        }

        // --------------------------------------------------------------- entry --

        [MenuItem("Tools/Universe Creator/Diagnostics/Scan Prefabs", false, 940)]
        public static void ScanMenu()
        {
            int scanned;
            List<Finding> found = Scan(out scanned);
            Debug.Log(Describe(found, scanned));
        }

        [MenuItem("Tools/Universe Creator/Diagnostics/Fix Prefab LOD Renderers", false, 941)]
        public static void FixMenu()
        {
            string report;
            int levels = FixNullRenderers(false, out report);
            AssetDatabase.Refresh();
            Debug.Log("=== PREFAB LOD RENDERER FIX ===\n" + report);
            if (levels == 0) Debug.Log("Nothing to fix - no LOD level held a null renderer.");
        }

        /// <summary>Headless scan. Exits 1 when a defect is present.</summary>
        public static void ScanBatch()
        {
            int scanned;
            List<Finding> found = Scan(out scanned);

            int defects = 0;
            for (int i = 0; i < found.Count; i++)
            {
                if (IsDefect(found[i].Kind)) defects++;
            }

            string text = Describe(found, scanned);
            Debug.Log(text);
            Console.WriteLine(text);
            EditorApplication.Exit(defects == 0 ? 0 : 1);
        }

        /// <summary>Headless fix, then a scan so the log proves the result.</summary>
        public static void FixBatch()
        {
            string report;
            FixNullRenderers(false, out report);
            AssetDatabase.Refresh();
            AssetDatabase.SaveAssets();

            int scanned;
            List<Finding> found = Scan(out scanned);

            int defects = 0;
            for (int i = 0; i < found.Count; i++)
            {
                if (IsDefect(found[i].Kind)) defects++;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== PREFAB LOD RENDERER FIX ===");
            sb.Append(report);
            sb.AppendLine();
            sb.Append(Describe(found, scanned));

            Debug.Log(sb.ToString());
            Console.WriteLine(sb.ToString());
            EditorApplication.Exit(defects == 0 ? 0 : 1);
        }

        /// <summary>
        /// One block of text holding the whole result, defects first. This is the
        /// artefact a log keeps, so it says what to do and not only what is wrong.
        /// </summary>
        public static string Describe(List<Finding> found, int scanned)
        {
            int defects = 0;
            for (int i = 0; i < found.Count; i++)
            {
                if (IsDefect(found[i].Kind)) defects++;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== UC PREFAB INTEGRITY: " + (defects == 0 ? "PASS" : "FAIL") + " ===");
            sb.AppendLine("assets     : " + scanned + " (prefabs + imported models)");
            sb.AppendLine("defects    : " + defects);
            sb.AppendLine("advisories : " + (found.Count - defects));

            for (int i = 0; i < found.Count; i++)
            {
                if (!IsDefect(found[i].Kind)) continue;
                sb.AppendLine("  DEFECT " + found[i]);
            }
            for (int i = 0; i < found.Count; i++)
            {
                if (IsDefect(found[i].Kind)) continue;
                sb.AppendLine("  note   " + found[i]);
            }

            return sb.ToString();
        }
    }
}
