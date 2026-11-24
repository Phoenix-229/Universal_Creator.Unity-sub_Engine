using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor.Utilities
{
    /// <summary>Separates game-only natural content from UC toolkit exports and audits dependency-preserving sample cleanup.</summary>
    /// <remarks>Explicit main-thread authoring only. Auditing changes no assets; asset rights and rendered quality require separate verification.</remarks>
    public static class UCContentDistribution
    {
        private const string Product = "Assets/UniverseCreator/";
        private const string Content = Product + "Content";
        private const string Sample = Content + "/Universal3DSample/";
        private static readonly string[] Categories = { "Grass", "Rocks", "Shrubs", "Surfaces", "Trees" };

        [Serializable]
        private sealed class Move
        {
            public string source, target, guid, sha256, metadataSha256;
            public string updatedMetadataSha256;
            public string[] dependencyGuids;
        }

        [Serializable]
        private sealed class CleanupPlan
        {
            public string project, createdUtc;
            public Move[] moves;
            public string[] removedAssets;
            public string[] replacedAssets;
            public Move[] createdFolders;
            public bool migrated;
        }

        internal static bool IsGameContent(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path = path.Replace('\\', '/');
            return path.Equals(Content, StringComparison.OrdinalIgnoreCase) || path.Equals(Content + ".meta", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(Content + "/", StringComparison.OrdinalIgnoreCase);
        }

        internal static string[] ToolkitAssets()
        {
            // Owner content policy: export explicit files, never recursive folders or IncludeDependencies that can pull borrowed game assets back in.
            string[] assets = AssetDatabase.GetAllAssetPaths().Where(path => path.StartsWith(Product, StringComparison.Ordinal) &&
                !IsGameContent(path) && !AssetDatabase.IsValidFolder(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (assets.Length == 0) throw new InvalidOperationException("No UC toolkit assets were found.");
            string borrowed = AssetDatabase.GetDependencies(assets, true).FirstOrDefault(IsGameContent);
            if (borrowed != null) throw new InvalidOperationException("A toolkit asset references game-only content: " + borrowed + ". Remove that reference before exporting.");
            return assets;
        }

        /// <summary>Prompts for a local unitypackage containing UC toolkit files with no game-only content or content dependencies.</summary>
        /// <exception cref="InvalidOperationException">The toolkit is empty or still references a game-only asset.</exception>
        /// <remarks>Main-thread Editor menu. Cancel creates nothing; exporting does not alter source assets or publish the package.</remarks>
        [MenuItem("Tools/Universe Creator/Export Toolkit (Exclude Game Content)")]
        public static void ExportToolkit()
        {
            string[] assets = ToolkitAssets();
            string target = EditorUtility.SaveFilePanel("Export UC toolkit without game content", "", "UniverseCreator", "unitypackage");
            if (string.IsNullOrEmpty(target)) return;
            AssetDatabase.ExportPackage(assets, target, ExportPackageOptions.Default);
            Debug.Log("UC toolkit exported " + assets.Length + " explicit assets without game content: " + target);
        }

        /// <summary>Audits sample natural assets and the complete dependency closure, writing a hashed move plan to the project's Logs directory.</summary>
        /// <remarks>Isolated batch entry point, main thread. Requires imported UC assets and a Logs junction to the owner's main log folder.
        /// Plans replacement of existing Grass/Rocks/Shrubs/Trees assets while preserving existing Surfaces files; it does not move/delete assets.
        /// Exits zero only for a complete plan with preserved GUIDs and no unsupported geometry or deleted-content dependency; one on failure.</remarks>
        /// <exception cref="InvalidOperationException">Invoked outside batch mode.</exception>
        public static void RunNaturalContentAuditBatch()
        {
            RunContentBatch(false);
        }

        /// <summary>Moves the audited natural dependency closure and removes unused sample assets inside an isolated UC validation project.</summary>
        /// <remarks>Main-thread batch entry point. Creates destination folders through Unity, preserves moved asset GUIDs/data/metadata hashes,
        /// and records generated folder metadata for synchronizing the owner workspace. Existing Surfaces assets remain unchanged.
        /// Never operates on the owner's main project; exits zero only for complete verified migration, one on failure.</remarks>
        /// <exception cref="InvalidOperationException">Invoked outside batch mode.</exception>
        public static void RunNaturalContentMigrationBatch()
        {
            RunContentBatch(true);
        }

        /// <summary>Flattens imported assets into one SampleNatural folder per category, preserving original Surfaces files and GUIDs.</summary>
        /// <remarks>Main-thread isolated batch entry point. Filename collisions use a stable GUID suffix; no existing asset is overwritten.
        /// Verifies dependencies and content hashes, removes emptied nested folders and applies the existing texture budget only to moved assets.
        /// Writes the exact move/importer manifest under the main Logs junction; exits zero on success, one on failure.</remarks>
        /// <exception cref="InvalidOperationException">Invoked outside batch mode.</exception>
        public static void RunNaturalContentFlattenBatch()
        {
            RunContentBatch(true, true);
        }

        private static void RunContentBatch(bool migrate, bool flatten = false)
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Run the content audit in an isolated batch project.");
            int code = 1;
            try
            {
                string project = Path.GetDirectoryName(Application.dataPath);
                if (string.IsNullOrEmpty(project)) throw new InvalidOperationException("The imported Unity project path is unavailable.");
                string[] assets = AssetDatabase.GetAllAssetPaths().Where(path => !AssetDatabase.IsValidFolder(path)).ToArray();
                var selected = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string path in assets)
                {
                    string category = flatten ? Categories.FirstOrDefault(value => path.StartsWith(Content + "/" + value + "/SampleNatural/", StringComparison.Ordinal)) : NaturalCategory(path);
                    if (category != null) selected.Add(path, category);
                }
                if (selected.Count == 0) throw new InvalidOperationException("No natural assets were found for this operation.");
                // Compare current importer contracts on both sides: a cached ShaderGraph can omit defaults until its first reimport after a move.
                foreach (string path in selected.Keys.Where(value => value.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase)))
                {
                    string dataHash = Hash(Path.Combine(project, path)), metadataHash = Hash(Path.Combine(project, path + ".meta"));
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                    if (Hash(Path.Combine(project, path)) != dataHash || Hash(Path.Combine(project, path + ".meta")) != metadataHash)
                        throw new InvalidOperationException("Shader importer changed authored data before the move: " + path);
                }
                // Owner clarification: old assets in four categories are replaced; Surfaces and all toolkit assets must remain valid.
                string[] retained = assets.Where(path => path.StartsWith(Product, StringComparison.Ordinal) &&
                    !path.StartsWith(Sample, StringComparison.Ordinal) && (flatten || !IsReplacedContent(path))).ToArray();
                foreach (string dependency in AssetDatabase.GetDependencies(selected.Keys.Concat(retained).ToArray(), true))
                {
                    if (!flatten && IsReplacedContent(dependency))
                        throw new InvalidOperationException("A retained asset depends on old content requested for deletion; replace that dependency before cleanup: " + dependency);
                    if (!dependency.StartsWith(Sample, StringComparison.Ordinal) || selected.ContainsKey(dependency)) continue;
                    string category = NaturalCategory(dependency);
                    if (category == null)
                    {
                        string extension = Path.GetExtension(dependency).ToLowerInvariant();
                        // Keep only actual supporting material/texture/shader dependencies. Unknown models/prefabs need inspection, not a guessed nature label.
                        if (!new[] { ".mat", ".shader", ".shadergraph", ".shadersubgraph", ".hlsl", ".cginc", ".png", ".tga", ".jpg", ".jpeg", ".exr", ".psd", ".tif", ".tiff", ".cubemap" }.Contains(extension))
                            throw new InvalidOperationException("Inspect this retained natural asset's unsupported dependency before cleanup: " + dependency);
                        category = "Surfaces";
                    }
                    selected.Add(dependency, category);
                }
                var moves = new List<Move>();
                var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in selected.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    string guid = AssetDatabase.AssetPathToGUID(entry.Key);
                    if (string.IsNullOrEmpty(guid) || !File.Exists(Path.Combine(project, entry.Key + ".meta")))
                        throw new InvalidOperationException("The source has no persistent asset identity: " + entry.Key);
                    // Latest owner layout: one flat SampleNatural folder per category; retain distinct same-name asset identities.
                    string target = Content + "/" + entry.Value + "/SampleNatural/" + Path.GetFileName(entry.Key);
                    if (target == entry.Key) continue;
                    if (targets.Contains(target) || File.Exists(Path.Combine(project, target)) || File.Exists(Path.Combine(project, target + ".meta")))
                        target = Content + "/" + entry.Value + "/SampleNatural/" + guid.Substring(0, 8) + "_" + Path.GetFileName(entry.Key);
                    if (!targets.Add(target) || File.Exists(Path.Combine(project, target)) || File.Exists(Path.Combine(project, target + ".meta")))
                        throw new InvalidOperationException("Cleanup would overwrite an existing asset: " + target);
                    moves.Add(new Move { source = entry.Key, target = target, guid = guid, sha256 = Hash(Path.Combine(project, entry.Key)), metadataSha256 = Hash(Path.Combine(project, entry.Key + ".meta")),
                        dependencyGuids = AssetDatabase.GetDependencies(entry.Key, true).Select(AssetDatabase.AssetPathToGUID).OrderBy(value => value, StringComparer.Ordinal).ToArray() });
                }
                var plan = new CleanupPlan { project = project, createdUtc = DateTime.UtcNow.ToString("o"), moves = moves.ToArray(),
                    replacedAssets = flatten ? Array.Empty<string>() : assets.Where(IsReplacedContent).OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                    removedAssets = assets.Where(path => path.StartsWith(Sample, StringComparison.Ordinal) && !selected.ContainsKey(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray() };
                string logs = Path.Combine(plan.project, "Logs"); Directory.CreateDirectory(logs);
                string report = Path.Combine(logs, (flatten ? "UC_ISOLATED_NATURAL_CONTENT_FLATTEN_" : migrate ? "UC_ISOLATED_NATURAL_CONTENT_MIGRATION_" : "UC_ISOLATED_NATURAL_CONTENT_PLAN_") + DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff") + ".log");
                // Persist the audited inputs before mutation so an interrupted move can be diagnosed or recovered exactly.
                File.WriteAllText(report, JsonUtility.ToJson(plan, true));
                if (migrate) Migrate(plan, flatten);
                File.WriteAllText(report, JsonUtility.ToJson(plan, true));
                Debug.Log("UC natural content audit: retained moves=" + moves.Count + "; removed sample assets=" + plan.removedAssets.Length +
                    "; replaced old assets=" + plan.replacedAssets.Length + "; report=" + report);
                code = 0;
            }
            catch (Exception error) { Debug.LogException(error); }
            EditorApplication.Exit(code);
        }

        private static void Migrate(CleanupPlan plan, bool flatten)
        {
            if (plan.project.Replace('\\', '/').IndexOf("/.work/UC_validation_project_", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Migration is restricted to the isolated UC validation copy.");
            // GUID references survive moves; literal source paths and relative shader includes require explicit review first.
            foreach (string path in AssetDatabase.GetAllAssetPaths().Where(value => value.StartsWith(Product, StringComparison.Ordinal)))
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (!new[] { ".cs", ".shader", ".hlsl", ".cginc", ".shadergraph", ".shadersubgraph" }.Contains(extension)) continue;
                string text = File.ReadAllText(Path.Combine(plan.project, path));
                if (plan.moves.Any(move => text.Contains(move.source))) throw new InvalidOperationException("Review a literal moved-asset path in " + path);
                if ((extension == ".hlsl" || extension == ".shader" || extension == ".cginc") && plan.moves.Any(move => move.source == path) &&
                    System.Text.RegularExpressions.Regex.IsMatch(text, "#\\s*include\\s*\"(?!Packages/|Assets/)"))
                    throw new InvalidOperationException("Review a relative shader include before moving " + path);
            }
            var folders = new List<Move>();
            foreach (Move move in plan.moves) EnsureFolder(Path.GetDirectoryName(move.target).Replace('\\', '/'), plan.project, folders);
            // Explicit authoring burst: defer repeated imports, then validate against Unity's final imported identities.
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (Move move in plan.moves)
                {
                    if (AssetDatabase.AssetPathToGUID(move.source) != move.guid || Hash(Path.Combine(plan.project, move.source)) != move.sha256 ||
                        Hash(Path.Combine(plan.project, move.source + ".meta")) != move.metadataSha256)
                        throw new InvalidOperationException("An audited source changed before migration: " + move.source);
                    string error = AssetDatabase.MoveAsset(move.source, move.target);
                    if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException("Could not move " + move.source + ": " + error);
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (Move move in plan.moves)
            {
                if (AssetDatabase.AssetPathToGUID(move.target) != move.guid || Hash(Path.Combine(plan.project, move.target)) != move.sha256 ||
                    Hash(Path.Combine(plan.project, move.target + ".meta")) != move.metadataSha256)
                    throw new InvalidOperationException("Moved asset identity/content changed: " + move.target);
                string[] dependencies = AssetDatabase.GetDependencies(move.target, true).Select(AssetDatabase.AssetPathToGUID).OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (!dependencies.SequenceEqual(move.dependencyGuids))
                    throw new InvalidOperationException("Moved asset dependency change: " + move.target + "; removed=" + string.Join(",", move.dependencyGuids.Except(dependencies)) +
                        "; added=" + string.Join(",", dependencies.Except(move.dependencyGuids).Select(AssetDatabase.GUIDToAssetPath)));
            }
            string[] survivors = AssetDatabase.GetAllAssetPaths().Where(path => path.StartsWith(Product, StringComparison.Ordinal) &&
                !path.StartsWith(Sample, StringComparison.Ordinal) && !AssetDatabase.IsValidFolder(path)).ToArray();
            string unresolved = AssetDatabase.GetDependencies(survivors, true).FirstOrDefault(path => path.StartsWith(Sample, StringComparison.Ordinal));
            if (unresolved != null) throw new InvalidOperationException("A retained asset still depends on the sample folder: " + unresolved);
            string[] roots = flatten ? Categories.SelectMany(category => Directory.EnumerateDirectories(Path.Combine(plan.project, Content, category, "SampleNatural"), "*", SearchOption.TopDirectoryOnly))
                .Select(path => path.Substring(plan.project.Length + 1).Replace('\\', '/')).ToArray() : new[] { Sample.TrimEnd('/') };
            foreach (string root in roots)
            {
                if (flatten && Directory.EnumerateFiles(Path.Combine(plan.project, root), "*", SearchOption.AllDirectories).Any(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Import folder still contains files: " + root);
                if (AssetDatabase.IsValidFolder(root) && !AssetDatabase.DeleteAsset(root)) throw new InvalidOperationException("Could not remove emptied import folder: " + root);
            }
            if (flatten)
            {
                var movedPaths = new HashSet<string>(plan.moves.Select(move => move.target), StringComparer.Ordinal);
                int changed = UCTextureBudget.Apply(false, out _, movedPaths);
                if (UCTextureBudget.Apply(true, out _, movedPaths) != 0) throw new InvalidOperationException("Moved textures still fail the existing import policy.");
                foreach (Move move in plan.moves) move.updatedMetadataSha256 = Hash(Path.Combine(plan.project, move.target + ".meta"));
                Debug.Log("UC category flatten: texture importers corrected=" + changed + "; original assets excluded from importer writes.");
            }
            AssetDatabase.SaveAssets();
            foreach (Move folder in folders)
                if (AssetDatabase.AssetPathToGUID(folder.target) != folder.guid || Hash(Path.Combine(plan.project, folder.target + ".meta")) != folder.metadataSha256)
                    throw new InvalidOperationException("Generated destination folder identity changed: " + folder.target);
            plan.createdFolders = folders.ToArray(); plan.migrated = true;
        }

        private static void EnsureFolder(string folder, string project, List<Move> created)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            if (!folder.StartsWith(Content + "/", StringComparison.Ordinal)) throw new InvalidOperationException("Destination leaves UC game content: " + folder);
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent, project, created);
            string guid = AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            if (string.IsNullOrEmpty(guid)) throw new InvalidOperationException("Could not create destination folder: " + folder);
            created.Add(new Move { target = folder, guid = guid, metadataSha256 = Hash(Path.Combine(project, folder + ".meta")) });
        }

        private static bool IsReplacedContent(string path)
        {
            return Categories.Any(category => category != "Surfaces" && path.StartsWith(Content + "/" + category + "/", StringComparison.Ordinal));
        }

        private static string NaturalCategory(string path)
        {
            if (!path.StartsWith(Sample, StringComparison.Ordinal)) return null;
            string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            bool vegetation = path.StartsWith(Sample + "Scenes/Oasis/Art/Vegetation/", StringComparison.Ordinal) ||
                path.StartsWith(Sample + "Scenes/Garden/Art/Vegetation/", StringComparison.Ordinal);
            if (vegetation)
            {
                if (name.Contains("grass") || name.Contains("liriope")) return "Grass";
                if (name.Contains("palm") || name.Contains("tree") || name.Contains("bark") || name.Contains("trunk")) return "Trees";
                return "Shrubs";
            }
            bool environment = path.StartsWith(Sample + "Scenes/Oasis/Art/Environment/", StringComparison.Ordinal) ||
                path.StartsWith(Sample + "Scenes/Garden/Art/Terrain/", StringComparison.Ordinal);
            if (!environment || name.Contains("shadowcage") || name.Contains("walkway") || name.Contains("tile") || name.Contains("brick")) return null;
            if (name.Contains("rock") || name.Contains("cliff") || name.Contains("mountain"))
                return Path.GetExtension(path).Equals(".fbx", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".prefab", StringComparison.OrdinalIgnoreCase) ? "Rocks" : "Surfaces";
            if (name.Contains("terrain") || name.Contains("ground") || name.Contains("grass") || name.Contains("gravel") || name.Contains("sand") ||
                name.Contains("soil") || name.Contains("dirt") || name.Contains("moss") || name.Contains("skybox")) return "Surfaces";
            return null;
        }

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path); using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        }
    }
}
