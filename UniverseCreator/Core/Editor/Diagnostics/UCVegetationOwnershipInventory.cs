// UC-T06-01: explicit read-only inventory reuses the catalog and native serialized references; no generation or retirement occurs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Optimization;
using UniverseCreator.Editor.Inspectors;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Diagnostics
{
    internal static class UCVegetationOwnershipInventory
    {
        internal sealed class Owner
        {
            internal Type Type;
            internal string Area, Decision, Tasks, Reason, Domains, Destination, Regression;
            internal readonly List<string> Sources = new List<string>();
        }
        internal sealed class GraphUse
        {
            internal string Asset, NodeId;
            internal Type Type;
            internal UCGraphDomain Domain;
        }
        internal sealed class Reference
        {
            internal string Property, Asset;
            internal UnityEngine.Object Value;
        }
        internal sealed class Report
        {
            internal readonly List<Owner> Owners = new List<Owner>();
            internal readonly List<GraphUse> Graphs = new List<GraphUse>();
            internal readonly List<Reference> References = new List<Reference>();
            internal readonly List<string> Diagnostics = new List<string>();
            internal bool Complete => Diagnostics.Count == 0;
        }

        [MenuItem("Tools/Universe Creator/Diagnostics/Vegetation Ownership Inventory")]
        private static void InspectSelectedWorld()
        {
            var world = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<UniverseCreatorObject>(true) : null;
            var report = Capture(world);
            var text = new StringBuilder("UC-T06-01 read-only vegetation ownership inventory\n");
            foreach (var owner in report.Owners)
                text.AppendLine(owner.Type.FullName + " | " + owner.Decision + " | " + owner.Tasks + " | " + owner.Reason + " | " + owner.Domains +
                    " | " + owner.Destination + " | " + owner.Regression + " | " + string.Join(", ", owner.Sources));
            foreach (var use in report.Graphs)
                text.AppendLine(use.Asset + " | " + use.Domain + " | " + use.NodeId + " | " + use.Type.FullName);
            foreach (var reference in report.References)
                text.AppendLine(reference.Property + " | " + (reference.Value == null ? "Empty content slot" :
                    string.IsNullOrEmpty(reference.Asset) ? "Unsaved native reference: " + reference.Value.name : reference.Asset));
            foreach (var diagnostic in report.Diagnostics) text.AppendLine("Incomplete: " + diagnostic);
            Debug.Log(text.ToString());
        }

        /// <summary>Inventories discovered vegetation/shared nodes, runtime owners, graph uses and one world's native prototype references.</summary>
        /// <param name="world">Selected world; null leaves the prototype-reference portion explicitly incomplete.</param>
        /// <param name="assetRoots">Existing folders below Assets, or null for Assets/UniverseCreator. At most 4,096 graphs and 2,048 nodes per graph.</param>
        /// <returns>Detached read-only records; diagnostics identify missing owners/types or exceeded limits instead of claiming complete coverage.</returns>
        /// <exception cref="ArgumentException">An asset root is outside Assets or does not exist.</exception>
        /// <remarks>Main thread, explicit invocation only. Never applies serialized properties, resolves ecology, runs a graph or deletes an asset.</remarks>
        internal static Report Capture(UniverseCreatorObject world, string[] assetRoots = null)
        {
            GraphExecutionGuard.RequireMainThread("Vegetation ownership inventory");
            string[] roots = assetRoots ?? new[] { "Assets/UniverseCreator" };
            if (roots.Length == 0 || roots.Any(r => string.IsNullOrEmpty(r) || r != "Assets" && !r.StartsWith("Assets/", StringComparison.Ordinal) ||
                r.Contains("..") || !AssetDatabase.IsValidFolder(r))) throw new ArgumentException("Choose existing project asset folders.", nameof(assetRoots));
            var report = new Report();
            var catalog = NodeCatalog.All.ToDictionary(e => e.type);
            Type[] types;
            try { types = typeof(Generator).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error)
            {
                types = error.Types.Where(t => t != null).ToArray();
                report.Diagnostics.Add("Runtime type loading was incomplete; restore unavailable dependencies before retiring any owner.");
            }
            var discovered = new HashSet<Type>(types.Where(t => !t.IsNested && !t.IsDefined(typeof(CompilerGeneratedAttribute), false) &&
                (IsRuntimeOwner(t) || catalog.TryGetValue(t, out var entry) && GraphDomainRules.Supports(entry.graphDomains, UCGraphDomain.Vegetation))));
            foreach (var entry in catalog.Values)
                if (entry.category == NodeCategory.Scatter || GraphDomainRules.Supports(entry.graphDomains, UCGraphDomain.Vegetation)) discovered.Add(entry.type);
            discovered.UnionWith(new[] { typeof(UCVegetationSystemManagerInspector), typeof(UCVegetationDiagnostics), typeof(UCVegetationValidation),
                typeof(ScatterApplier.AppearanceUpdate), typeof(ScatterApplier.Replacement), typeof(GPUInstancingManager.OwnerReplacement),
                typeof(UCVegetationSystemManager.RegistrationReplacement) }); // UC-T06-12: nested native/transaction owners are explicitly inventoried.
            foreach (var type in discovered.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                var owner = Classify(type);
                owner.Domains = catalog.TryGetValue(type, out var entry) ? entry.graphDomains.ToString() : "Shared data/consumer; no independent graph execution";
                report.Owners.Add(owner);
            }
            CaptureSourceEvidence(report);
            var relevant = new HashSet<Type>(report.Owners.Select(o => o.Type));
            string[] guids = AssetDatabase.FindAssets("t:Graph", roots);
            if (guids.Length > 4096) report.Diagnostics.Add("Graph inventory exceeds 4,096 assets; inspect narrower folders before declaring coverage complete.");
            foreach (string guid in guids.Take(4096).OrderBy(g => g, StringComparer.Ordinal))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Graph graph = AssetDatabase.LoadAssetAtPath<Graph>(path);
                if (graph == null || graph.nodes == null) { report.Diagnostics.Add("Unavailable graph: " + path); continue; }
                if (graph.unresolvedNodes != null && graph.unresolvedNodes.Count > 0) report.Diagnostics.Add("Unresolved authored types remain preserved in " + path);
                if (graph.nodes.Count > 2048) { report.Diagnostics.Add("Graph exceeds the 2,048-node inventory limit: " + path); continue; }
                foreach (var node in graph.nodes)
                    if (node == null) report.Diagnostics.Add("An unresolved node slot remains in " + path);
                    else if (relevant.Contains(node.GetType())) report.Graphs.Add(new GraphUse { Asset = path, Domain = graph.Domain, NodeId = node.id, Type = node.GetType() });
            }
            CapturePrototypeReferences(world, report);
            return report;
        }

        private static bool IsRuntimeOwner(Type type)
        {
            if (type.Namespace == "UniverseCreator.Ecology" || type.Namespace == "UniverseCreator.Nodes.Scatter" ||
                type.Name.StartsWith("UCVegetation", StringComparison.Ordinal) || type.Name.StartsWith("GraphVegetation", StringComparison.Ordinal)) return true;
            return type == typeof(ScatterPrototypeLibrary) || type == typeof(TreePrototypeDef) || type == typeof(ShrubPrototypeDef) ||
                type == typeof(DetailPrototypeDef) || type == typeof(ObjectPrototypeDef) || type == typeof(ScatterRemovalStore) ||
                type == typeof(VegetationInstanceKey) || type == typeof(ScatterIdentitySnapshot) || // UC-T06-06: identity consumers need inventory destinations too.
                type == typeof(ScatterGroundBinding) || // UC-T06-07: retained contact data follows the shared native applier.
                type == typeof(ScatterPrototypeVariation) || type == typeof(ScatterScaleDistribution) ||
                type == typeof(UCScatterColliderPart) || type == typeof(ScatterCollisionCategory) || // UC-T06-08: shared variation/physical categories are retained owners.
                type == typeof(ScatterApplier) || type == typeof(EcoScatterContextBuilder) || type == typeof(UCWorldGraphBindings) ||
                type == typeof(ScatterApplier.AppearanceUpdate) || // UC-T06-09: native tint state has an exact bounded consumer and retirement lifetime.
                type == typeof(ScatterApplier.Replacement) || type == typeof(UCScatterRegionResource) ||
                type == typeof(GPUInstancingManager.OwnerReplacement) || type == typeof(UCVegetationSystemManager.RegistrationReplacement) || // UC-T06-12: staged/scene owners have exact destinations too.
                type == typeof(UCWindManager) || type == typeof(GPUInstancingManager) || type == typeof(ChunkManager) ||
                type == typeof(ScatterInstance) || type == typeof(PoissonDisc) || type == typeof(PoissonSettings) ||
                type == typeof(ScatterCandidate) || type == typeof(SpatialHash) || type == typeof(ScatterFilter) ||
                type == typeof(RangeFilter) || type == typeof(FilterCombine) || type == typeof(VegetationClass) ||
                type == typeof(UCShrubInstance) || type == typeof(UniverseCreator.Overlay.MicroReliefScatterBias) ||
                type == typeof(UniverseCreator.Overlay.ScatterMicrositeAffinity) || type == typeof(UniverseCreator.Nodes.Outputs.WindOutputNode);
        }

        private static Owner Classify(Type type)
        {
            var owner = new Owner { Type = type, Area = type.Namespace, Decision = "Keep", Tasks = "UC-T06-01/14",
                Reason = "Retain the existing shared data/numerical owner and serialization; regress its callers before any retirement.",
                Destination = type.FullName,
                Regression = "Extend the registered ecology/graph fixtures with the consuming placement path; compare cold, retained and region-revisit outputs before changing this owner." };
            if (typeof(Generator).IsAssignableFrom(type))
            {
                if (type.Namespace == "UniverseCreator.Nodes.Scatter")
                {
                    owner.Decision = "Migrate/improve"; owner.Tasks = "UC-T06-02/04/05/17";
                    owner.Reason = "Preserve IDs, seeds and prototype slots; use accepted world inputs and bounded placement/rejection diagnostics.";
                    if (type.Name == "ObjectScatterNode" || type.Name == "RockScatterNode" || type.Name == "DebrisScatterNode")
                    { owner.Decision = "Keep/improve"; owner.Tasks = "UC-T06-07/08/16"; owner.Reason = "Generic/deposit consumers share placement infrastructure; retain useful non-vegetation graph access and category-specific controls."; }
                    if (type.Name == "UnderwaterScatterNode") { owner.Tasks = "UC-T06-02/11"; owner.Reason = "Migrate aquatic authoring while retaining submerged/emergent rules and diagnosing unavailable water observations."; }
                    if (type.Name == "VegetationRegionMaskNode") { owner.Decision = "Keep"; owner.Tasks = "UC-T06-03/14"; owner.Reason = "Regional masks use absolute world metres and the existing scatter Mask consumer; no duplicate world or placement engine."; }
                    if (type.Name == "WindSwayParameterBakeNode" || type.Name == "GroundCoverColourTintNode")
                    { owner.Decision = "Keep/improve"; owner.Tasks = "UC-T06-09"; owner.Reason = "Appearance parameters must reach existing consumers without reseeding or regenerating placement."; }
                }
                else { owner.Tasks = "UC-T06-03/04/14"; owner.Reason = "Reuse the catalog's typed shared utility/field owner; domain admission does not create a second evaluator."; }
                owner.Destination = type.FullName + "; retain its serialized loader and shared implementation, author placement in Vegetation Graph";
                owner.Regression = "Extend TestVegetationGraphBinding and TestGraphMigrationExecution: preserve node IDs/seeds/settings, prototype references, authored wires and final placement; rollback must preserve the original graph.";
                if (type.Name == "ObjectScatterNode" || type.Name == "RockScatterNode" || type.Name == "DebrisScatterNode")
                    owner.Destination = type.FullName + "; shared generic/deposit placement remains available outside plant authoring";
                return owner;
            }
            switch (type.Name)
            {
                case "Replacement": case "OwnerReplacement": case "RegistrationReplacement": case "UCScatterRegionResource":
                    owner.Decision = "Keep/improve"; owner.Tasks = "UC-T06-12/14/18"; owner.Reason = "Stage native/renderer/registration resources through existing owners; preserve exact scene region references and full identity across reload.";
                    owner.Regression = "TestVegetationOwnershipLifetime exercises unpublished native rollback, independent region retirement, saved parking/removal state and revisit."; break;
                case "ScatterRemovalStore": case "ScatterInstance": case "VegetationInstanceKey": case "ScatterIdentitySnapshot": // UC-T06-06
                    owner.Decision = "Improve/migrate"; owner.Tasks = "UC-T06-06/18"; owner.Reason = "XZ-only 31-bit identity and int removal storage require a versioned world/layer/species identity with explicit legacy recovery."; break;
                case "ScatterPrototypeVariation": case "ScatterScaleDistribution": // UC-T06-08: retain opt-in transforms over stable keys.
                    owner.Decision = "Keep/improve"; owner.Tasks = "UC-T06-08/16"; owner.Reason = "Validate deterministic size/yaw overlays without reseeding placement or replacing existing species/allometry selection.";
                    owner.Regression = "TestVegetationVariationAndCollision covers bounded distributions, full-key collision independence, saved settings and actual native application."; break;
                case "UCScatterColliderPart": case "ScatterCollisionCategory": // UC-T06-08: explicit structural and visual categories.
                    owner.Decision = "Keep/improve"; owner.Tasks = "UC-T06-08/13"; owner.Reason = "Reuse authored structural shapes; exclude leaf/grass collision and preserve native prefab ownership.";
                    owner.Regression = "TestVegetationVariationAndCollision covers inactive visual parts, structural triggers, native tree rejection and inherited-shape removal."; break;
                case "ScatterApplier": case "AppearanceUpdate": case "GPUInstancingManager": case "ChunkManager": // UC-T06-09
                    owner.Decision = "Improve"; owner.Tasks = "UC-T06-07/09/10/12"; owner.Reason = "Keep actual native consumers; preserve placement during bounded appearance updates, then improve spatial batching and exact owner retirement."; break;
                case "UCVegetationSystemManager": case "UCWorldGraphBindings": case "EcoScatterContextBuilder": case "EcoScatterContext":
                case "UCVegetationSystemManagerInspector": case "UCVegetationValidation": case "UCVegetationDiagnostics":
                    owner.Decision = "Improve"; owner.Tasks = "UC-T06-03/15/16/17"; owner.Reason = "Unify graph/manager authoring and accepted revisions through existing ownership; retain cached resolve, registration and diagnostics."; break;
                case "GraphVegetationRegion": case "GraphVegetationResult": case "GraphVegetationPatch":
                    owner.Decision = "Keep/improve"; owner.Tasks = "UC-T06-03/12/14"; owner.Reason = "Retain accepted world/region outputs and patch collection ownership; regress moved/stale regions, partial replacement and exact retirement."; break;
                case "SpeciesSuitability": case "EnvironmentSampler": case "WaterProbe": case "HydroField": case "HydroExclusion": case "SpeciesSelector": case "VegetationWaterHabitat": // UC-T06-11: typed water-domain restrictions reuse the habitat owner.
                    owner.Decision = "Improve"; owner.Tasks = "UC-T06-04/11"; owner.Reason = "Preserve physical suitability and explicit missing channels; validate supported habitat/water observations and finite inputs."; break;
                case "PoissonDisc": case "PoissonSettings": case "ScatterCandidate": case "SpatialHash":
                case "ScatterFilter": case "RangeFilter": case "FilterCombine": case "MicroReliefScatterBias": case "ScatterMicrositeAffinity":
                case "DispersalField": case "StandStructure": case "ZoneSpacing": case "CanopyLightGrid": case "CanopyLight":
                    owner.Decision = "Improve"; owner.Tasks = "UC-T06-05/17"; owner.Reason = "Retain ecological clustering/competition; verify deterministic world keys, border halos, stable quality subsets and bounded queries."; break;
                case "PlacementClearance": case "CutRegionRegistry":
                    owner.Decision = "Improve"; owner.Tasks = "UC-T06-07/12/18"; owner.Reason = "Reuse plugin-neutral ground/cut clearance and ownership while preserving manual intent and category-specific seating."; break;
                case "Phenology": case "UCWindManager": case "UCVegetationInteractor": case "UCShrubInstance":
                    owner.Decision = "Improve"; owner.Tasks = "UC-T06-09/13"; owner.Reason = "Keep shared seasonal/wind/contact consumers and separate appearance updates from discrete placement."; break;
                case "ScatterPrototypeLibrary": case "TreePrototypeDef": case "ShrubPrototypeDef": case "DetailPrototypeDef": case "ObjectPrototypeDef": case "EcoEnvelope":
                    owner.Decision = "Improve"; owner.Tasks = "UC-T06-08/13/16"; owner.Reason = "Preserve authored content/envelope layouts; validate real pivots, materials, wind, LOD and collision categories through native references."; break;
            }
            return owner;
        }

        // UC-T06-01: declarations supply evidence for multi-type/partial source files too; an unlocated source is a gap, never a guessed path.
        private static void CaptureSourceEvidence(Report report)
        {
            string[] scripts = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets/UniverseCreator/Core" });
            if (scripts.Length > 4096) report.Diagnostics.Add("Source inventory exceeds 4,096 scripts; source coverage is incomplete.");
            var declarations = new Regex(@"^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe)\s+)*(?:class|struct|enum|interface)\s+(\w+)\b", RegexOptions.Multiline);
            var namespaces = new Regex(@"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline);
            foreach (string guid in scripts.Take(4096).OrderBy(g => g, StringComparer.Ordinal))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("/_backup/") || path.Contains("/_retired/")) continue;
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null) { report.Diagnostics.Add("Unavailable source: " + path); continue; }
                string source = script.text;
                if (source.Length > 1048576) { report.Diagnostics.Add("Source exceeds the 1 MiB declaration limit: " + path); continue; }
                var spaces = namespaces.Matches(source).Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
                foreach (Match declaration in declarations.Matches(source))
                    foreach (var owner in report.Owners)
                        if (owner.Type.Name == declaration.Groups[1].Value && spaces.Contains(owner.Type.Namespace))
                            owner.Sources.Add(path + ":" + (1 + source.Take(declaration.Index).Count(c => c == '\n')));
            }
            foreach (var owner in report.Owners)
            {
                owner.Sources.Sort(StringComparer.Ordinal);
                if (owner.Sources.Count == 0) report.Diagnostics.Add("No shipped source declaration located for " + owner.Type.FullName + "; inspect plugin/package evidence before retirement.");
            }
        }

        private static void CapturePrototypeReferences(UniverseCreatorObject world, Report report)
        {
            if (world == null) { report.Diagnostics.Add("Select a world to inspect its actual prototype references; catalog records alone do not prove authored usage."); return; }
            using (var serialized = new SerializedObject(world))
            {
                var library = serialized.FindProperty("scatterLibrary");
                if (library == null) { report.Diagnostics.Add("The selected world has no serialized scatter library."); return; }
                var property = library.Copy(); var end = library.GetEndProperty(); int visited = 0;
                while (property.Next(true) && !SerializedProperty.EqualContents(property, end))
                {
                    if (++visited > 16384) { report.Diagnostics.Add("Prototype inventory exceeds 16,384 properties; inspect smaller authored libraries before claiming completeness."); break; }
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = property.objectReferenceValue;
                    report.References.Add(new Reference { Property = property.propertyPath, Value = value, Asset = value != null ? AssetDatabase.GetAssetPath(value) : string.Empty });
                }
            }
        }
    }
}
