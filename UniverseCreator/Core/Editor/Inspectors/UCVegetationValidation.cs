// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UCVegetationValidation.cs
//  Purpose: The pre-build validation panel for the vegetation inspector (V-51).
//
//  WHY THIS EXISTS
//  ---------------
//  The failure mode that costs the most time is a silent empty world: a library
//  that is wired wrongly places nothing, throws nothing, and logs nothing, so the
//  author spends an hour looking at the graph before they look at the envelope.
//  Every check here turns one of those silences into a named, actionable row.
//
//  Two kinds of check live here, and the difference is deliberate:
//
//    - Pure library/manager checks, answerable with no build at all: a null
//      prefab, a contradictory envelope, a fade length of zero. These are the
//      common case and they run every repaint against the serialized data.
//      The collision-shape checks (VEG-LEAF1) are in this group: they read the
//      prototype prefab's collider components and never touch the asset.
//
//    - The graph-output check, which CANNOT be answered before a build, because
//      the moisture/biome/water/derivative products are filled by a bake and not
//      by the resolve. So it reads the last bake's mask, reported by ChunkManager
//      through UCVegetationSystemManager.ReportMissingChannels, and says "not
//      built yet" honestly rather than guessing.
//
//  It shares EnvironmentSampler.MissingFrom with the bake, so the panel asks the
//  same question the sampler asks rather than a second, drifting copy of it.
//
//  NO DIALOGS. Every result is a row in the inspector.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UniverseCreator.Ecology;
using UniverseCreator.TerrainSystem;
using UniverseCreator.Nodes;          // Graph, Generator (scatter-node presence check)
using UniverseCreator.Nodes.Scatter;  // ScatterNodeBase (Step 31)

namespace UniverseCreator.Editor.Inspectors
{
    /// <summary>
    /// Draws the vegetation library's validation rows. Static and stateless: it
    /// reads the manager and its world every repaint, so a fix shows up the moment
    /// the field changes rather than after a re-select.
    /// </summary>
    public static partial class UCVegetationValidation // UC-T06-13: explicit prototype inspection shares this existing inspector owner.
    {
        private enum Severity { Info, Warning, Error }

        private struct Defect
        {
            public string message;
            public Severity severity;

            public Defect(string message, Severity severity)
            {
                this.message = message;
                this.severity = severity;
            }
        }

        // Folded state lives in EditorPrefs so it survives a reselect, matching the
        // grouped inspector's sections.
        private const string FoldKey = "UniverseCreator.Inspector.Vegetation.Validation";

        /// <summary>
        /// The whole panel: a foldout with a count in its header, then one row per
        /// defect, or a single green line when the library is clean.
        /// </summary>
        public static void Draw(UCVegetationSystemManager v)
        {
            if (v == null) return;

            List<Defect> defects = Collect(v);

            EditorGUILayout.Space(4);

            bool open = EditorPrefs.GetBool(FoldKey, true);
            string heading = defects.Count == 0
                ? "Validation   (clean)"
                : "Validation   (" + defects.Count + ")";

            // Amber when there is something to look at, grey when there is not, so
            // the header alone tells the author whether to open it.
            Color accent = defects.Count == 0
                ? UCInspectorUI.HeaderGrey
                : UCInspectorUI.HeaderAmber;

            bool next = UCInspectorUI.Section(open, heading, accent);
            if (next != open) EditorPrefs.SetBool(FoldKey, next);
            if (!next) return;

            DrawPrototypeTools(v.World); // UC-T06-13: mesh/material inspection and reference creation run only on explicit clicks.

            if (defects.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No problems found in the scatter library. This checks wiring, not " +
                    "taste: a clean library can still grow a forest you did not intend.",
                    MessageType.Info);
                return;
            }

            for (int i = 0; i < defects.Count; i++)
            {
                EditorGUILayout.HelpBox(defects[i].message, Map(defects[i].severity));
            }
        }

        private static MessageType Map(Severity s)
        {
            if (s == Severity.Error) return MessageType.Error;
            if (s == Severity.Warning) return MessageType.Warning;
            return MessageType.Info;
        }

        // =====================================================================
        //  COLLECTION
        // =====================================================================

        private static List<Defect> Collect(UCVegetationSystemManager v)
        {
            List<Defect> defects = new List<Defect>();

            UniverseCreatorObject world = v.World;
            ScatterPrototypeLibrary lib = world != null ? world.scatterLibrary : null;

            if (world == null)
            {
                defects.Add(new Defect(
                    "This vegetation manager is not under a Universe Creator world, so " +
                    "there is no library to validate and nothing will scatter.",
                    Severity.Error));
                return defects;
            }

            if (lib == null)
            {
                defects.Add(new Defect(
                    "The world has no scatter library assigned. Assign one on the " +
                    "Universe Creator object, or every scatter node places nothing.",
                    Severity.Warning));
                // The graph-output check still applies: a world can be wired without
                // a library and still fail to produce the masks the scatter needs.
                CheckGraphOutputs(v, defects);
                return defects;
            }

            // The two loudest silences, checked before anything about individual
            // species (Step 31). These are the exact reasons a Regenerate or a
            // Generate Vegetation press "does nothing": there is no scatter node in
            // the graph to emit instances, or the library it would resolve is empty.
            // Both are content, not a broken pipeline - so they are named here rather
            // than left to a build that places nothing and logs nothing.
            CheckScatterNodePresent(world, defects);
            CheckLibraryNotEmpty(lib, defects);

            float worldHeightM = Mathf.Max(0f, world.maxHeightMeters);

            // The warmest the world can get anywhere: sea level (lowest, so warmest
            // under the lapse rate), warm-facing slope, fully inland (widest swing).
            // Optimistic on purpose - it is the ceiling, so a species rejected
            // against it is rejected everywhere, and we would rather miss a marginal
            // case than cry wolf on a plausible one.
            EcoSampleSettings settings = v.ResolveSettings();
            float worldWarmestMonthC =
                settings.MeanTempAtC(0f) +
                Mathf.Max(0f, settings.aspectWarmingC) +
                Mathf.Max(0f, settings.annualTempHalfAmplitudeC);

            CheckTrees(lib, worldHeightM, worldWarmestMonthC, defects);
            CheckShrubs(lib, worldHeightM, worldWarmestMonthC, defects);
            CheckDetails(lib, worldHeightM, worldWarmestMonthC, defects);
            CheckObjects(lib, worldHeightM, worldWarmestMonthC, defects);

            CheckColliderBudget(lib, defects);
            CheckBillboardFade(v, defects);
            CheckDetailMemory(v, defects);
            CheckGraphOutputs(v, defects);

            return defects;
        }

        // =====================================================================
        //  PER-LIST PROTOTYPE CHECKS
        // =====================================================================

        private static void CheckTrees(ScatterPrototypeLibrary lib, float worldHeightM,
                                       float worldWarmestMonthC, List<Defect> defects)
        {
            for (int i = 0; i < lib.trees.Count; i++)
            {
                TreePrototypeDef def = lib.trees[i];
                if (def == null) continue;

                string who = Label("Tree", i, def.eco);

                if (def.prefab == null)
                    defects.Add(NullPrefab(who));

                CheckEnvelope(who, def.eco, worldHeightM, worldWarmestMonthC, defects);
                CheckFoliageCollision(who, def.prefab, true, defects);
            }
        }

        private static void CheckShrubs(ScatterPrototypeLibrary lib, float worldHeightM,
                                        float worldWarmestMonthC, List<Defect> defects)
        {
            for (int i = 0; i < lib.shrubs.Count; i++)
            {
                ShrubPrototypeDef def = lib.shrubs[i];
                if (def == null) continue;

                string who = Label("Shrub", i, def.eco);

                if (def.prefab == null)
                    defects.Add(NullPrefab(who));

                CheckEnvelope(who, def.eco, worldHeightM, worldWarmestMonthC, defects);

                // A shrub's trigger is built by UCShrubInstance on the ROOT, reusing a root
                // capsule if the prefab ships one. A collider on a CHILD is never touched, so
                // foliage collision authored there survives the build exactly as authored.
                CheckFoliageCollision(who, def.prefab, false, defects);
            }
        }

        private static void CheckDetails(ScatterPrototypeLibrary lib, float worldHeightM,
                                         float worldWarmestMonthC, List<Defect> defects)
        {
            for (int i = 0; i < lib.details.Count; i++)
            {
                DetailPrototypeDef def = lib.details[i];
                if (def == null) continue;

                string who = Label("Detail", i, def.eco);

                // A detail is valid with a mesh OR a billboard texture; only both
                // absent is the null case.
                if (def.prefabMesh == null && def.billboardTexture == null)
                    defects.Add(new Defect(
                        who + " has neither a mesh nor a billboard texture, so its " +
                        "detail layer is skipped and every Grass node after it in the " +
                        "list feeds the wrong terrain layer.",
                        Severity.Warning));

                CheckEnvelope(who, def.eco, worldHeightM, worldWarmestMonthC, defects);
                CheckInertDetailCollision(who, def.prefabMesh, defects);
            }
        }

        private static void CheckObjects(ScatterPrototypeLibrary lib, float worldHeightM,
                                         float worldWarmestMonthC, List<Defect> defects)
        {
            for (int i = 0; i < lib.objects.Count; i++)
            {
                ObjectPrototypeDef def = lib.objects[i];
                if (def == null) continue;

                string who = Label("Object", i, def.eco);

                if (def.prefab == null)
                    defects.Add(NullPrefab(who));

                CheckEnvelope(who, def.eco, worldHeightM, worldWarmestMonthC, defects);
                CheckFoliageCollision(who, def.prefab, false, defects);
            }
        }

        // =====================================================================
        //  COLLISION SHAPE  (VEG-LEAF1)
        // =====================================================================
        //
        //  WHAT A TREE'S COLLISION SHOULD BE
        //  ---------------------------------
        //  A real tree stops a body at its trunk and at its big limbs. It does not
        //  stop one at a leaf. Unreal/SpeedTree authors this as a handful of
        //  primitive capsules and spheres on the trunk and the main branches, and
        //  the leaf cards carry no collision at all - so a character brushes
        //  through the canopy and is stopped dead only by the wood.
        //
        //  A mesh collider over tree geometry is the opposite of that: every leaf
        //  triangle in the mesh becomes collision, so the canopy is a solid wall
        //  with leaf-shaped holes in it, and the cost is the whole mesh rather than
        //  three primitives. That is the single thing this check is looking for.
        //
        //  WHY IT ONLY REPORTS
        //  -------------------
        //  The prefab is the buyer's content. Stripping a collider here would
        //  rewrite an asset the author may have authored deliberately (a mesh
        //  collider on a hollow log is correct), and a validation panel that edits
        //  assets on repaint is a different and much worse bug than the one it
        //  fixes. So every finding is a row: named, actionable, and the author's
        //  decision. The build-time counterpart is ColliderPolicy, which decides
        //  WHETHER collision is kept at all - grass None, shrub Trigger, tree
        //  Solid - and already strips colliders on the spawned INSTANCE when the
        //  policy says None (ScatterApplier.StripColliders).
        //
        //  NAME-FREE BY CONSTRUCTION: nothing here looks at an object's name, a
        //  path, or a material name to decide what is a leaf. It reads collider
        //  TYPES and counts, which mean the same thing in any content set.

        /// <summary>
        /// What is wrong with one vegetation prefab's collision. Flags rather than a
        /// single verdict, because the caller decides which of them matter for its
        /// channel - "no collision" is a defect on a tree and the normal case on a
        /// shrub, whose trigger UC builds itself.
        /// </summary>
        [System.Flags]
        public enum FoliageCollisionIssue
        {
            None = 0,
            /// <summary>A mesh collider lacks an explicit structural-part category and may collide foliage.</summary>
            MeshCollider = 1 << 0,
            /// <summary>Worse: a non-convex one, i.e. per-triangle against leaf cards.</summary>
            ConcaveMeshCollider = 1 << 1,
            /// <summary>Nothing stops a body anywhere on the prefab.</summary>
            NoCollision = 1 << 2,
            /// <summary>More colliders than a trunk and its limbs need.</summary>
            TooMany = 1 << 3,
            /// <summary>UC-T06-08: an explicitly visual/excluded part still has an authored collider.</summary>
            NonCollidingPart = 1 << 4,
            /// <summary>UC-T06-08: an authored part category is outside the supported enum.</summary>
            UnknownPart = 1 << 5
        }

        /// <summary>
        /// A trunk plus its main limbs. Past this, the extra shapes are usually on
        /// foliage. Deliberately generous: a well-authored tree with buttress roots
        /// and three big branches must not trip it.
        /// </summary>
        public const int TrunkColliderBudget = 8;

        /// <summary>
        /// Reads one prefab's collision. PURE AND PREFAB-SAFE: it only gets
        /// components and never instantiates, moves, adds or destroys anything, so
        /// it is legal against a prefab asset from a repaint. Counts include
        /// inactive children, because an inactive collider still ships and an
        /// author who enables that branch at runtime gets the collision back.
        /// UC-T06-08: explicit trunk/major-branch/rock markers distinguish structural mesh
        /// shapes from unclassified foliage; counts still include every real collider.
        /// </summary>
        /// <param name="prefab">Borrowed prefab root, including inactive children; null returns no issues and zero counts.</param>
        /// <param name="total">Output count of all authored collider components.</param>
        /// <param name="meshColliders">Output count of mesh collider components, including structural parts.</param>
        /// <param name="concaveMeshColliders">Output count of nonconvex mesh colliders.</param>
        /// <returns>Combined collision-authoring issues; the prefab is never modified.</returns>
        public static FoliageCollisionIssue InspectFoliageCollision(
            GameObject prefab, out int total, out int meshColliders, out int concaveMeshColliders)
        {
            total = 0;
            meshColliders = 0;
            concaveMeshColliders = 0;
            int unclassifiedMesh = 0;
            FoliageCollisionIssue issues = FoliageCollisionIssue.None;

            // A null prefab already has its own row; saying it has no collision too
            // would be two rows for one fault.
            if (prefab == null) return FoliageCollisionIssue.None;

            Collider[] cols = prefab.GetComponentsInChildren<Collider>(true);
            if (cols != null)
            {
                for (int i = 0; i < cols.Length; i++)
                {
                    Collider c = cols[i];
                    if (c == null) continue;

                    total++;
                    // UC-T06-08: inspect explicit component data, never object/material names or guessed geometry.
                    var category = UCScatterColliderPart.Read(c, prefab.transform);
                    if ((int)category < 0 || (int)category > (int)ScatterCollisionCategory.None) issues |= FoliageCollisionIssue.UnknownPart;
                    if (UCScatterColliderPart.IsNonColliding(category)) issues |= FoliageCollisionIssue.NonCollidingPart;

                    MeshCollider mc = c as MeshCollider;
                    if (mc == null) continue;

                    meshColliders++;
                    if (category != ScatterCollisionCategory.Trunk && category != ScatterCollisionCategory.MajorBranch && category != ScatterCollisionCategory.Rock)
                        unclassifiedMesh++;
                    if (!mc.convex) concaveMeshColliders++;
                }
            }

            if (total == 0) issues |= FoliageCollisionIssue.NoCollision;
            if (unclassifiedMesh > 0) issues |= FoliageCollisionIssue.MeshCollider;
            if (concaveMeshColliders > 0) issues |= FoliageCollisionIssue.ConcaveMeshCollider;
            if (total > TrunkColliderBudget) issues |= FoliageCollisionIssue.TooMany;

            return issues;
        }

        /// <summary>
        /// The rows for one prefab's collision. <paramref name="wantsTrunkCollision"/>
        /// is true only where the channel's collision comes from the prefab and
        /// nowhere else - a tree. A shrub's trigger is built by UCShrubInstance, so
        /// an empty shrub prefab is correct and must stay silent.
        /// </summary>
        private static void CheckFoliageCollision(string who, GameObject prefab,
                                                  bool wantsTrunkCollision, List<Defect> defects)
        {
            int total, mesh, concave;
            FoliageCollisionIssue issues =
                InspectFoliageCollision(prefab, out total, out mesh, out concave);

            if (issues == FoliageCollisionIssue.None) return;

            // UC-T06-08: native tree prototypes are borrowed; their forbidden authored shapes must be corrected explicitly.
            if ((issues & (FoliageCollisionIssue.NonCollidingPart | FoliageCollisionIssue.UnknownPart)) != 0)
                defects.Add(new Defect(who + " has collision on an explicitly visual/excluded or unsupported part. " +
                    "Remove leaf/grass colliders from the authored tree prefab; keep marked trunk/major-branch shapes. " +
                    "Native tree registration rejects this content without changing the source asset.", Severity.Warning));
            if ((issues & FoliageCollisionIssue.ConcaveMeshCollider) != 0 && (issues & FoliageCollisionIssue.MeshCollider) == 0)
                defects.Add(new Defect(who + " uses non-convex structural mesh collision. Use a small capsule/box or a validated convex shape " +
                    "for trunks and major branches; the part marker identifies purpose and does not reduce physics cost.", Severity.Warning));

            if ((issues & FoliageCollisionIssue.MeshCollider) != 0)
            {
                bool bad = (issues & FoliageCollisionIssue.ConcaveMeshCollider) != 0;

                defects.Add(new Defect(
                    who + " has " + mesh + " mesh collider" + (mesh == 1 ? "" : "s") +
                    " on its prefab. A mesh collider over plant geometry makes the LEAF " +
                    "CARDS solid along with the wood: every leaf triangle becomes collision, " +
                    "so a body is stopped in mid-air at the canopy edge and the per-plant " +
                    "cost is the whole mesh instead of a few primitives. " +
                    (bad
                        ? concave + " of them " + (concave == 1 ? "is" : "are") + " not convex, " +
                          "which is per-triangle collision against leaf cards - the most " +
                          "expensive shape there is, on the part of the plant that should " +
                          "carry none. "
                        : "") +
                    "Collide the trunk and the main branches with capsules or boxes and leave " +
                    "the foliage with no collider at all.",
                    Severity.Warning));
            }

            if ((issues & FoliageCollisionIssue.TooMany) != 0)
                defects.Add(new Defect(
                    who + " carries " + total + " colliders. A trunk and its main limbs need " +
                    "only a few (" + TrunkColliderBudget + " is the budget here), so this many " +
                    "usually means the foliage is being collided as well. Check that none of " +
                    "them sits on a leaf card.",
                    Severity.Info));

            if (wantsTrunkCollision && (issues & FoliageCollisionIssue.NoCollision) != 0)
                defects.Add(new Defect(
                    who + " has no collider anywhere on its prefab, so nothing stops a body at " +
                    "the trunk - a character walks straight through the tree. A terrain tree " +
                    "takes its collision from the prefab (Universe Creator attaches none of its " +
                    "own), so the shape has to be authored there.",
                    Severity.Info));
        }

        /// <summary>
        /// A terrain detail is drawn, never instantiated, so a collider on its mesh
        /// is never created by anything. Reported because an author who put one there
        /// believes their grass is solid, and it is not - the parting a player feels
        /// walking through a meadow is entirely in the vertex shader.
        /// </summary>
        private static void CheckInertDetailCollision(string who, GameObject prefabMesh,
                                                      List<Defect> defects)
        {
            if (prefabMesh == null) return;

            Collider[] cols = prefabMesh.GetComponentsInChildren<Collider>(true);
            if (cols == null) return;

            int count = 0;
            for (int i = 0; i < cols.Length; i++) if (cols[i] != null) count++;
            if (count == 0) return;

            defects.Add(new Defect(
                who + " is a terrain detail whose mesh carries " + count + " collider" +
                (count == 1 ? "" : "s") + ". Details are drawn by the terrain and never " +
                "instantiated as GameObjects, so " + (count == 1 ? "it is" : "they are") +
                " never created and " + (count == 1 ? "does" : "do") + " nothing. Grass is " +
                "meant to have no collision - a walker parts it in the vertex shader, which " +
                "is why a million blades cost nothing. Remove " +
                (count == 1 ? "it" : "them") + " from the mesh, or make this species a shrub " +
                "if it really has to be walked into.",
                Severity.Info));
        }

        // =====================================================================
        //  ENVELOPE CHECKS  (shared by every prototype kind)
        // =====================================================================

        /// <summary>
        /// The contradictions that make an envelope impossible to satisfy - the ones
        /// that grow nothing and say nothing. Every bound here is read the same way
        /// the placement pass reads it, so a row means the placement pass will also
        /// reject the species, not merely that the numbers look odd.
        /// </summary>
        private static void CheckEnvelope(string who, EcoEnvelope eco, float worldHeightM,
                                          float worldWarmestMonthC, List<Defect> defects)
        {
            if (eco == null) return;

            // A disabled species is off on purpose; a contradiction inside it is not
            // costing anyone a build, so stay quiet about it.
            if (!eco.enabled) return;

            // UC-T06-04: use the production boundary rule so nonfinite authoring explains an empty bake.
            if (!SpeciesSuitability.HasValidHabitatParameters(eco))
                defects.Add(new Defect(who + ": habitat contains nonfinite values, negative tolerance widths or inverted active bounds. " +
                    "The runtime rejects this species until its habitat settings are valid.", Severity.Error));

            // Lethal band inverted. SpeciesSuitability rejects below min and above
            // max, so min above max rejects everything in between - which is
            // everything.
            if (eco.tempLethalMinC > eco.tempLethalMaxC)
                defects.Add(new Defect(
                    who + ": lethal minimum " + eco.tempLethalMinC.ToString("0.#") +
                    " degC is above lethal maximum " + eco.tempLethalMaxC.ToString("0.#") +
                    " degC. No temperature satisfies both, so the species is rejected " +
                    "everywhere.",
                    Severity.Error));

            // Riparian band inverted. distanceToWaterMaxM below zero means "ignore",
            // so only a non-negative max can contradict the min.
            if (eco.distanceToWaterMaxM >= 0f &&
                eco.distanceToWaterMinM > eco.distanceToWaterMaxM)
                defects.Add(new Defect(
                    who + ": keep-out from water " + eco.distanceToWaterMinM.ToString("0.#") +
                    " m is beyond the riparian limit " + eco.distanceToWaterMaxM.ToString("0.#") +
                    " m. The band is empty - nowhere is both far enough from the water " +
                    "and close enough to it.",
                    Severity.Error));

            // Absolute altitude band. Only bites when the toggle is on; left relative,
            // the thermal gates place the species and this band is unused.
            if (eco.useAbsoluteAltitude)
            {
                if (eco.minAltitudeM >= eco.maxAltitudeM)
                    defects.Add(new Defect(
                        who + ": absolute altitude band is empty (" +
                        eco.minAltitudeM.ToString("0") + " m up to " +
                        eco.maxAltitudeM.ToString("0") + " m). Nothing sits in a band " +
                        "with no height in it.",
                        Severity.Error));
                else if (eco.minAltitudeM > worldHeightM)
                    defects.Add(new Defect(
                        who + ": absolute altitude band starts at " +
                        eco.minAltitudeM.ToString("0") + " m, above the world's tallest " +
                        "possible ground at " + worldHeightM.ToString("0") + " m. The " +
                        "species can never be high enough to appear.",
                        Severity.Warning));
            }

            // Korner's treeline against the warmest the world can offer. If the
            // species needs a warmer high-summer than anywhere reaches, it is absent
            // everywhere - the mountain-goes-bare rule turned all the way up.
            if (eco.warmestMonthMinC > worldWarmestMonthC)
                defects.Add(new Defect(
                    who + ": needs a warmest-month mean of at least " +
                    eco.warmestMonthMinC.ToString("0.#") + " degC, but the warmest the " +
                    "world reaches is about " + worldWarmestMonthC.ToString("0.#") +
                    " degC (sea level, sun-facing, inland). The species is above the " +
                    "world's treeline everywhere.",
                    Severity.Warning));
        }

        // =====================================================================
        //  LIBRARY-WIDE AND MANAGER CHECKS
        // =====================================================================

        private static void CheckColliderBudget(ScatterPrototypeLibrary lib, List<Defect> defects)
        {
            if (!lib.HasShrubs) return;

            // A budget of zero drops every shrub trigger, so nothing a walker should
            // bump into is solid. Not an empty world, but a silently non-interactive
            // one, which is the same class of time sink.
            if (lib.shrubColliderBudget <= 0)
            {
                defects.Add(new Defect(
                    "Shrub collider budget is " + lib.shrubColliderBudget + ", so no " +
                    "shrub keeps its trigger. Walkers pass through every bush. Raise the " +
                    "budget if shrubs are meant to be solid.",
                    Severity.Warning));
                return;
            }

            // A budget larger than the shrub cap can never bind - there are never
            // that many shrubs in a chunk to give colliders to. Harmless, but it
            // means the number is not doing what its author thinks.
            if (lib.maxShrubsPerChunk > 0 && lib.shrubColliderBudget > lib.maxShrubsPerChunk)
                defects.Add(new Defect(
                    "Shrub collider budget (" + lib.shrubColliderBudget + ") is above the " +
                    "per-chunk shrub cap (" + lib.maxShrubsPerChunk + "), so it can never " +
                    "be reached. Every shrub already keeps its trigger; the extra budget " +
                    "does nothing.",
                    Severity.Info));
        }

        private static void CheckBillboardFade(UCVegetationSystemManager v, List<Defect> defects)
        {
            // Authored zero is silently raised to the minimum cross-fade by the
            // resolve, so the world does not pop - but the author's zero meant "no
            // fade", and they should know it is being overridden rather than honoured.
            if (v.billboardFadeLengthMetres == 0f)
                defects.Add(new Defect(
                    "Billboard fade length is 0 m. A hard mesh-to-billboard switch pops " +
                    "visibly, so the resolve raises it to the " +
                    UCVegetationSystemManager.MinCrossFadeMetres.ToString("0") +
                    " m minimum. Set a fade length to control it yourself.",
                    Severity.Info));
        }

        private static void CheckDetailMemory(UCVegetationSystemManager v, List<Defect> defects)
        {
            // Reuse the manager's own ceiling test rather than re-deriving the bytes,
            // so the panel and the memory readout can never disagree about whether
            // the library is over budget.
            if (!v.DetailMemoryOverCeiling) return;

            int fits = v.DetailResolutionUnderCeiling;

            defects.Add(new Defect(
                "Detail layers would hold " + v.ResolvedDetailMemoryMB.ToString("0") +
                " MB, over the " + v.detailMemoryCeilingMB.ToString("0") + " MB ceiling. " +
                "This is RAM held while the chunks are resident, on screen or not. " +
                (fits >= 8
                    ? "A detail resolution of " + fits + " fits within the ceiling."
                    : "No usable resolution fits: reduce the detail species count or the " +
                      "world size."),
                Severity.Warning));
        }

        /// <summary>
        /// No scatter node in the graph means the bake emits no instances at all, so
        /// nothing scatters regardless of how full the library is. This is the first
        /// thing to check when a world "spawns nothing" (Step 31).
        /// </summary>
        private static void CheckScatterNodePresent(UniverseCreatorObject world, List<Defect> defects)
        {
            Graph g = world.graph;
            if (g == null || g.nodes == null || g.nodes.Count == 0)
            {
                defects.Add(new Defect(
                    "This world has no graph, so nothing - terrain or vegetation - is " +
                    "generated. Assign a graph on the Universe Creator object.",
                    Severity.Error));
                return;
            }

            bool hasScatter = false;
            for (int i = 0; i < g.nodes.Count; i++)
            {
                if (g.nodes[i] is ScatterNodeBase) { hasScatter = true; break; }
            }

            if (!hasScatter)
                defects.Add(new Defect(
                    "The graph has no scatter node (Tree, Shrub, Grass, Rock or Object " +
                    "Scatter), so the bake produces no vegetation instances no matter " +
                    "what the library holds. Add a scatter node fed by the height output, " +
                    "then generate.",
                    Severity.Error));
        }

        /// <summary>
        /// A library with no prototypes in any of the four lists scatters nothing even
        /// with a scatter node present - the per-type appliers just skip. The other
        /// half of the "spawns nothing" answer (Step 31).
        /// </summary>
        private static void CheckLibraryNotEmpty(ScatterPrototypeLibrary lib, List<Defect> defects)
        {
            int trees = lib.trees != null ? lib.trees.Count : 0;
            int shrubs = lib.shrubs != null ? lib.shrubs.Count : 0;
            int details = lib.details != null ? lib.details.Count : 0;
            int objects = lib.objects != null ? lib.objects.Count : 0;

            if (trees + shrubs + details + objects == 0)
                defects.Add(new Defect(
                    "The scatter library is empty: no trees, shrubs, grass or objects are " +
                    "authored, so a build places nothing. Add at least one prototype in " +
                    "the Prototype library panel above.",
                    Severity.Error));
        }

        private static void CheckGraphOutputs(UCVegetationSystemManager v, List<Defect> defects)
        {
            if (!v.HasBakeReport)
            {
                defects.Add(new Defect(
                    "Graph wiring is not checked until the world builds. Build it once to " +
                    "see whether the moisture, biome, water and derivative products the " +
                    "ecology reads are actually being produced.",
                    Severity.Info));
                return;
            }

            EnvironmentChannel missing = v.LastMissingChannels;
            if (missing == EnvironmentChannel.None) return;

            defects.Add(new Defect(
                "The last chunk to build was missing these graph products: " +
                Describe(missing) + ". A species that reads a missing product falls back " +
                "to a stated default, which is how a graph wired without a heightmap " +
                "quietly grows a flat meadow at sea level. Wire the outputs, or the " +
                "ecology is running half-blind.",
                Severity.Warning));
        }

        // =====================================================================
        //  HELPERS
        // =====================================================================

        private static Defect NullPrefab(string who)
        {
            return new Defect(
                who + " has no prefab. The slot holds its index so the ones after it do " +
                "not shift, but it places nothing.",
                Severity.Warning);
        }

        private static string Label(string kind, int index, EcoEnvelope eco)
        {
            if (eco != null && !string.IsNullOrEmpty(eco.displayName))
                return kind + " \"" + eco.displayName + "\"";
            return kind + " #" + index;
        }

        /// <summary>
        /// A readable list of the missing channels. The enum's own ToString gives the
        /// flag names; this adds the one-word reason each matters, because "Water" on
        /// its own does not tell an author what breaks without it.
        /// </summary>
        private static string Describe(EnvironmentChannel missing)
        {
            List<string> parts = new List<string>();

            if ((missing & EnvironmentChannel.Height) != 0)
                parts.Add("Height (altitude gating)");
            if ((missing & EnvironmentChannel.Moisture) != 0)
                parts.Add("Moisture (drought and wetland limits)");
            if ((missing & EnvironmentChannel.Biome) != 0)
                parts.Add("Biome (per-biome weighting)");
            if ((missing & EnvironmentChannel.Water) != 0)
                parts.Add("Water (shoreline and riparian bands)");
            if ((missing & EnvironmentChannel.Derivatives) != 0)
                parts.Add("Derivatives (slope, aspect, wetness index)");

            if (parts.Count == 0) return missing.ToString();

            return string.Join(", ", parts.ToArray());
        }
    }
}
