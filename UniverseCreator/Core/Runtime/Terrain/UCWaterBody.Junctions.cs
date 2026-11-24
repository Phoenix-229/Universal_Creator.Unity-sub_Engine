// UC-T04-05: the river owns persistent source/mouth records; inverse lists are derived compatibility data.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators; // UC-T04-07: retire the existing owned graph-course publication.

namespace UniverseCreator.TerrainSystem
{
    public partial class UCWaterBody
    {
        [SerializeField, HideInInspector] private string stableWaterBodyId;
        [SerializeField, HideInInspector] private List<WaterJunctionRecord> waterJunctions = new List<WaterJunctionRecord>();
        // UC-T04-07: native publication witnesses are runtime-only; serialized acceptance cannot survive reload.
        private readonly Dictionary<UCWaterBody, WaterBodyData> junctionReceiverWitnesses = new Dictionary<UCWaterBody, WaterBodyData>();
        private GenerationRunId junctionAcceptedRun;
        private UniverseCreatorObject waterPublicationWorld; // UC-T04-07: captured ownership survives a grouping/world change.
        [SerializeField, HideInInspector] private string nodeManagedMouthId;
        [SerializeField, HideInInspector] private string automaticMouthId;

        /// <summary>Persistent scene-water GUID, created lazily on the main thread without moving or saving authored assets.</summary>
        public string StableWaterBodyId
        {
            get
            {
                GraphExecutionGuard.RequireMainThread("Water body identity");
                if (string.IsNullOrEmpty(stableWaterBodyId)) stableWaterBodyId = Guid.NewGuid().ToString("N");
                return stableWaterBodyId;
            }
        }

        /// <summary>Latest connection/ownership diagnostic, or empty for valid or unconnected endpoints.</summary>
        public string WaterJunctionDiagnostic { get; private set; } = string.Empty;

        /// <summary>Copies this river's persistent directed records; empty when no endpoint has a receiver/source.</summary>
        /// <returns>Independent records in source/mouth order. Saved geometry must be revalidated for the current runtime world.</returns>
        public WaterJunctionRecord[] GetWaterJunctionRecords()
        {
            GraphExecutionGuard.RequireMainThread("Water junction snapshot");
            var records = new WaterJunctionRecord[waterJunctions?.Count ?? 0];
            bool current = JunctionGeometryIsCurrent();
            for (int i = 0; i < records.Length; i++) records[i] = waterJunctions[i].CopyWithGeometry(current && waterJunctions[i].GeometryAccepted);
            return records;
        }

        private bool JunctionGeometryIsCurrent()
        {
            if (lastBuild == null || waterJunctions.Count == 0 || junctionReceiverWitnesses.Count == 0) return false;
            var world = CarveWorld;
            foreach (var record in waterJunctions) if (record.WorldOwner != world) return false;
            if (world != null && !world.GenerationScheduler.CurrentRun.Equals(junctionAcceptedRun)) return false;
            foreach (var witness in junctionReceiverWitnesses)
                if (!SameJunctionScope(witness.Key) || !witness.Key.enabled || !ReferenceEquals(witness.Key.lastBuild, witness.Value)) return false;
            return true;
        }

        private bool SameJunctionScope(UCWaterBody target)
        {
            return target != null && target != this && target.CarveWorld == CarveWorld && target.gameObject.scene == gameObject.scene;
        }

        // UC-T04-06: grouping transforms are authoring organization, not world ownership.
        private UCWaterBody[] ScopedJunctionBodies()
        {
            var bodies = new List<UCWaterBody>();
            var world = CarveWorld;
            if (world != null) bodies.AddRange(world.GetComponentsInChildren<UCWaterBody>(true));
            else if (gameObject.scene.IsValid() && gameObject.scene.isLoaded)
                foreach (var root in gameObject.scene.GetRootGameObjects()) bodies.AddRange(root.GetComponentsInChildren<UCWaterBody>(true));
            bodies.RemoveAll(body => !SameJunctionScope(body) || !body.enabled);
            bodies.Sort((a, b) => string.CompareOrdinal(a.StableWaterBodyId, b.StableWaterBodyId));
            return bodies.ToArray();
        }

        // UC-T04-06: sample the actual accepted triangle, including a sloping receiver.
        private static bool TryAcceptedSurfaceAt(UCWaterBody body, Vector3 point, out float level)
        {
            level = 0;
            WaterBodyData data = body?.lastBuild;
            if (data?.triangles == null || data.vertices == null) return false;
            bool found = false;
            for (int i = 0; i < data.triangles.Length; i += 3)
            {
                Vector3 a = body.transform.TransformPoint(data.vertices[data.triangles[i]]);
                Vector3 b = body.transform.TransformPoint(data.vertices[data.triangles[i + 1]]);
                Vector3 c = body.transform.TransformPoint(data.vertices[data.triangles[i + 2]]);
                double area = CrossXZ(a, b, c);
                if (System.Math.Abs(area) < 1e-8) continue;
                double u = CrossXZ(b, c, point) / area, v = CrossXZ(c, a, point) / area, w = 1 - u - v;
                if (u < -1e-6 || v < -1e-6 || w < -1e-6) continue;
                float y = (float)(u * a.y + v * b.y + w * c.y);
                if (!RiverChannelSnapshot.Finite(y) || found && Mathf.Abs(y - level) > 0.001f) return false;
                level = y; found = true;
            }
            return found;
        }

        private static double CrossXZ(Vector3 a, Vector3 b, Vector3 c) =>
            ((double)b.x - a.x) * ((double)c.z - a.z) - ((double)b.z - a.z) * ((double)c.x - a.x);

        private bool TryAutomaticRiverReceiver(Vector3 endpoint, out UCWaterBody receiver)
        {
            receiver = null;
            MouthSection(endpoint, out var left, out var right, out _);
            foreach (var body in ScopedJunctionBodies())
            {
                if (body.IsLevelBody || !AcceptedSectionTouches(body, left, right)) continue;
                if (receiver != null) { WaterJunctionDiagnostic = "River endpoint intersects multiple receiving rivers."; return false; }
                receiver = body;
            }
            return receiver != null;
        }

        private void MouthSection(Vector3 endpoint, out Vector3 left, out Vector3 right, out float width)
        {
            var nodes = OwnedRiverNodes(); width = carve != null ? carve.riverDefaultHalfWidthM : 1;
            foreach (var node in nodes) if (node.halfWidthM > 0 && RiverChannelSnapshot.Finite(node.halfWidthM)) width = node.halfWidthM;
            Vector3 direction = nodes.Length > 1 ? endpoint - nodes[nodes.Length - 2].transform.position : Vector3.forward;
            Vector2 normal = new Vector2(-direction.z, direction.x).normalized;
            var offset = new Vector3(normal.x * width, 0, normal.y * width); left = endpoint + offset; right = endpoint - offset;
        }

        // UC-T04-07: exact segment/triangle clipping detects a thin receiving ribbon
        // even when the incoming centre and bank endpoints all lie outside it.
        private static bool AcceptedSectionTouches(UCWaterBody target, Vector3 left, Vector3 right)
        {
            var data = target.lastBuild;
            if (data?.triangles == null || data.triangles.Length > 393216) return false;
            for (int i = 0; i < data.triangles.Length; i += 3)
            {
                Vector3 a = target.transform.TransformPoint(data.vertices[data.triangles[i]]);
                Vector3 b = target.transform.TransformPoint(data.vertices[data.triangles[i + 1]]);
                Vector3 c = target.transform.TransformPoint(data.vertices[data.triangles[i + 2]]);
                double area = CrossXZ(a, b, c); if (System.Math.Abs(area) < 1e-8) continue;
                double lo = 0, hi = 1, sign = area > 0 ? 1 : -1; bool intersects = true;
                for (int edge = 0; edge < 3; edge++)
                {
                    Vector3 start = edge == 0 ? a : edge == 1 ? b : c, end = edge == 0 ? b : edge == 1 ? c : a;
                    double first = CrossXZ(start, end, left) * sign, last = CrossXZ(start, end, right) * sign;
                    if (first < 0 && last < 0) { intersects = false; break; }
                    if ((first < 0) != (last < 0))
                    {
                        double t = first / (first - last);
                        if (first < 0) lo = System.Math.Max(lo, t); else hi = System.Math.Min(hi, t);
                    }
                    if (lo > hi) { intersects = false; break; }
                }
                if (intersects) return true;
            }
            return false;
        }

        // UC-T04-07: an authored course supplies a planning hint before its first
        // native surface exists. Final recognition/acceptance still uses wet triangles.
        private static bool TouchesPlannedRiver(UCWaterBody body, Vector3 endpoint, Vector3 left, Vector3 right, float incomingWidth)
        {
            if (body.IsLevelBody || body.shape != ShapeMode.RiverSpline) return false;
            if (body.lastBuild != null) return AcceptedSectionTouches(body, left, right);
            var nodes = body.OwnedRiverNodes();
            if (nodes.Length < 2 || nodes.Length > RiverCrossSectionFit.MaxStations) return false;
            float width = body.carve != null ? body.carve.riverDefaultHalfWidthM : 1;
            for (int i = 0; i + 1 < nodes.Length; i++)
            {
                if (nodes[i].halfWidthM > 0) width = nodes[i].halfWidthM;
                Vector3 a = nodes[i].transform.position, b = nodes[i + 1].transform.position;
                Vector2 ab = new Vector2(b.x - a.x, b.z - a.z), delta = new Vector2(endpoint.x - a.x, endpoint.z - a.z);
                if (!RiverChannelSnapshot.Finite(width) || ab.sqrMagnitude < 0.0001f) continue;
                float t = Mathf.Clamp01(Vector2.Dot(delta, ab) / ab.sqrMagnitude);
                // A second terminating tributary is not a downstream continuation.
                if (i == nodes.Length - 2 && t > 0.999f) continue;
                if ((delta - ab * t).sqrMagnitude <= (width + incomingWidth) * (width + incomingWidth)) return true;
            }
            return false;
        }

        private bool TryJoinRiverSurface(WaterBodyData incoming, out WaterBodyData joined)
        {
            junctionReceiverWitnesses.Clear(); // UC-T04-07: only this newly prepared receiver set may publish acceptance.
            joined = incoming;
            if (waterJunctions.Count == 0) return WaterJunctionDiagnostic.Length == 0;
            var bodies = ScopedJunctionBodies();
            foreach (var record in waterJunctions)
            {
                string targetId = record.AtSource ? record.FromBodyId : record.ToBodyId;
                UCWaterBody target = null;
                foreach (var candidate in bodies)
                {
                    if (candidate.StableWaterBodyId != targetId) continue;
                    if (target != null) { WaterJunctionDiagnostic = "Junction receiver identity is duplicated."; return false; }
                    target = candidate;
                }
                if (target == null || target.lastBuild == null)
                { WaterJunctionDiagnostic = "Junction is pending an accepted receiving surface."; return false; }
                // Receiver coverage owns the union, even when a lake supplies the river.
                // Limit correction by available depth; never grant a terrain write here.
                float clearance = Mathf.Max(0.01f, record.DepthMetres);
                if (!WaterJunctionMesh.TryJoin(joined, transform.localToWorldMatrix, target.lastBuild,
                    target.transform.localToWorldMatrix, clearance, out var clipped, out var diagnostic))
                { WaterJunctionDiagnostic = diagnostic; return false; }
                joined = clipped;
                junctionReceiverWitnesses[target] = target.lastBuild;
            }
            Terrain[] terrains = Terrain.activeTerrains; var world = CarveWorld;
            foreach (Vector3 vertex in joined.vertices)
            {
                Vector3 p = transform.TransformPoint(vertex); float ground = 0;
                bool valid = FollowTrench
                    ? world != null && RiverChannelRegistry.TrySampleGround(riverFitRun.WorldId, p.x, p.z, riverFitRun.Revision, out ground, out var run) && run.Equals(riverFitRun)
                    : TrySampleSurfaceGround(terrains, world, p.x, p.z, out ground);
                if (!valid || ground > p.y + 0.0001f)
                { WaterJunctionDiagnostic = "Joined river boundary leaves accepted wet ground."; return false; }
            }
            // UC-T04-06: changing boundary elevation can expose a dry wedge inside
            // an otherwise valid original triangle. Recheck the new triangulation.
            for (int i = 0; i < joined.triangles.Length; i += 3)
            {
                Vector3 a = transform.TransformPoint(joined.vertices[joined.triangles[i]]);
                Vector3 b = transform.TransformPoint(joined.vertices[joined.triangles[i + 1]]);
                Vector3 c = transform.TransformPoint(joined.vertices[joined.triangles[i + 2]]);
                if (Vector3.Cross(b - a, c - a).y <= 1e-8f)
                { WaterJunctionDiagnostic = "Joined river has a folded shared boundary."; return false; }
                for (int sample = 0; sample < 4; sample++)
                {
                    Vector3 p = sample == 0 ? (a + b + c) / 3 : sample == 1 ? (a + b) / 2 : sample == 2 ? (b + c) / 2 : (c + a) / 2;
                    float ground = 0;
                    bool valid = FollowTrench
                        ? world != null && RiverChannelRegistry.TrySampleGround(riverFitRun.WorldId, p.x, p.z, riverFitRun.Revision, out ground, out var run) && run.Equals(riverFitRun)
                        : TrySampleSurfaceGround(terrains, world, p.x, p.z, out ground);
                    if (!valid || ground > p.y + 0.0001f)
                    { WaterJunctionDiagnostic = "Joined river triangle bridges dry or missing ground."; return false; }
                }
            }
            WaterJunctionDiagnostic = string.Empty; return true;
        }

        private void AcceptWaterJunctionGeometry()
        {
            junctionAcceptedRun = CarveWorld != null ? CarveWorld.GenerationScheduler.CurrentRun : default;
            // UC-T04-06: BuildData is preparation; acceptance follows actual native publication.
            for (int i = 0; i < waterJunctions.Count; i++) waterJunctions[i] = waterJunctions[i].AcceptedCopy();
        }

        /// <summary>Returns this body's authored points in hierarchy order, including inactive points and excluding nested bodies.</summary>
        /// <returns>A new array of borrowed scene components; empty before the first point is placed.</returns>
        /// <remarks>UC-T04-05B: main-thread editor and runtime preparation share the same ownership filter.</remarks>
        public UCRiverNode[] OwnedRiverNodes()
        {
            var all = GetComponentsInChildren<UCRiverNode>(true);
            var owned = new List<UCRiverNode>(all.Length);
            foreach (var node in all) if (node != null && node.GetComponentInParent<UCWaterBody>(true) == this) owned.Add(node);
            return owned.ToArray();
        }

        private void ClearWaterJunctions(bool retainCandidates = false)
        {
            junctionReceiverWitnesses.Clear(); // UC-T04-07: no saved/runtime acceptance survives endpoint invalidation.
            if (sourceBody != null) sourceBody.riverOutlets?.Remove(this);
            sourceBody = null; mouthBody = null;
            if (waterJunctions == null) waterJunctions = new List<WaterJunctionRecord>();
            else if (!retainCandidates) waterJunctions.Clear();
            else for (int i = 0; i < waterJunctions.Count; i++) waterJunctions[i] = waterJunctions[i].CopyWithGeometry(false); // UC-T04-07: a missing receiver keeps its saved identity for Undo/reload.
            WaterJunctionDiagnostic = string.Empty;
        }

        // UC-T04-07: cancel captured authoring inputs before a deferred editor rebuild,
        // while retaining accepted ground and the last native surface until replacement.
        internal void InvalidatePendingWaterAuthoring(bool retainCandidates = false)
        {
            GraphExecutionGuard.RequireMainThread("Water authoring invalidation");
            ClearWaterJunctions(retainCandidates); // An actual course/target edit releases previous automatic endpoint identities.
            RiverCourseInputNode.UnpublishBodyCourse(GetEntityId().GetHashCode());
            var world = CarveWorld;
            if (world == null) return;
            world.CancelGeneration();
            world.GenerationScheduler.BeginRun();
        }

        private UCWaterBody SavedJunctionTarget(bool atSource)
        {
            // UC-T04-07: names/proximity never substitute for a retained endpoint identity.
            foreach (var record in waterJunctions)
            {
                if (record.AtSource != atSource) continue;
                string id = atSource ? record.FromBodyId : record.ToBodyId;
                UCWaterBody result = null;
                foreach (var body in ScopedJunctionBodies())
                    if (body.StableWaterBodyId == id) { if (result != null) return null; result = body; }
                return result;
            }
            return null;
        }

        private void ResolveMouthControl(UCRiverNode[] nodes)
        {
            // UC-T04-07: removing a child tick retires only that tick's former target;
            // a separate authored body-level reference remains authoritative.
            var node = nodes.Length > 0 ? nodes[nodes.Length - 1] : null;
            if (!string.IsNullOrEmpty(nodeManagedMouthId) && (node == null || !node.connectJunction || node.junctionTarget == null))
            {
                if (joinsRiver != null && joinsRiver.StableWaterBodyId == nodeManagedMouthId) joinsRiver = null;
                nodeManagedMouthId = null;
            }
            if (!string.IsNullOrEmpty(automaticMouthId) && joinsRiver != null && joinsRiver.StableWaterBodyId == automaticMouthId) joinsRiver = null;
            automaticMouthId = null;
            if (node != null && node.connectJunction && node.junctionTarget != null)
            { joinsRiver = node.junctionTarget; nodeManagedMouthId = node.junctionTarget.StableWaterBodyId; }
        }

        private void RetireJunctionDependents(UCWaterBody[] previousScope = null)
        {
            // UC-T04-07: deleting/clearing a receiver withdraws the entire affected
            // surface chain iteratively. Settings and authored points remain intact.
            var scope = previousScope ?? ScopedJunctionBodies();
            var retiring = new HashSet<UCWaterBody> { this }; var queue = new Queue<UCWaterBody>(); queue.Enqueue(this);
            while (queue.Count > 0)
            {
                var target = queue.Dequeue();
                foreach (var body in scope)
                {
                    if (body == null) continue;
                    if (retiring.Contains(body)) continue;
                    bool depends = body.junctionReceiverWitnesses.ContainsKey(target) || body.joinsRiver == target;
                    foreach (var record in body.waterJunctions)
                        if ((record.AtSource ? record.FromBodyId : record.ToBodyId) == target.StableWaterBodyId) depends = true;
                    if (!depends) continue;
                    retiring.Add(body); queue.Enqueue(body);
                }
            }
            foreach (var body in retiring)
            {
                if (body == this) continue;
                body.ClearWaterJunctions(true); body.ClearNativeGeometry();
                RiverCourseInputNode.UnpublishBodyCourse(body.GetEntityId().GetHashCode());
                body.WaterJunctionDiagnostic = "Connection receiver was cleared, disabled or unloaded; rebuild after it is available.";
            }
        }

        private void OnTransformParentChanged()
        {
            // UC-T04-07: group edits in the same world preserve the publication;
            // moving a built body to another world retires the old ownership first.
            if (lastBuild == null || waterPublicationWorld == CarveWorld) return;
            var previousWorld = waterPublicationWorld;
            if (previousWorld != null)
            {
                RetireJunctionDependents(previousWorld.GetComponentsInChildren<UCWaterBody>(true));
                if (previousWorld.isActiveAndEnabled) { previousWorld.CancelGeneration(); previousWorld.GenerationScheduler.BeginRun(); }
            }
            ClearWaterJunctions(); ClearNativeGeometry();
            RiverCourseInputNode.UnpublishBodyCourse(GetEntityId().GetHashCode());
            WaterJunctionDiagnostic = "Water world ownership changed; rebuild against the new world's accepted ground.";
        }

        internal void RebuildOrderedWaterBody()
        {
            // UC-T04-07: called only after dependency ordering by manager/editor/component owners.
            BuildDig(); RebuildSurface();
        }

        internal void RefreshOrderedWaterSurface()
        {
            // UC-T04-07: final native ground is authoritative. Read mode refreshes
            // its accepted profile; manual cuts keep their existing bed to avoid re-digging.
            if (shape == ShapeMode.RiverSpline && FollowTrench) BuildDig(false);
            RebuildSurface();
        }

        /// <summary>Orders enabled water bodies with receiving/source dependencies first, rejecting missing, foreign or cyclic dependencies.</summary>
        /// <param name="bodies">Borrowed bodies to order; null/disabled entries are skipped. Maximum 4096 distinct bodies.</param>
        /// <returns>A new deterministic array of valid bodies. Invalid bodies withdraw their owned surface and expose a diagnostic.</returns>
        /// <remarks>UC-T04-07: main thread only. No recursive generation or terrain write occurs while ordering.</remarks>
        public static UCWaterBody[] GetWaterRebuildOrder(UCWaterBody[] bodies)
            => CreateWaterRebuildOrder(bodies, out _);

        private static UCWaterBody[] CreateWaterRebuildOrder(UCWaterBody[] bodies,
            out Dictionary<UCWaterBody, HashSet<UCWaterBody>> dependencyMap)
        {
            GraphExecutionGuard.RequireMainThread("Water rebuild dependency order");
            var pending = new List<UCWaterBody>(); var ids = new Dictionary<string, UCWaterBody>();
            var supplied = new HashSet<UCWaterBody>();
            if (bodies != null) foreach (var body in bodies)
                if (body != null && body.enabled && supplied.Add(body)) pending.Add(body);
            if (pending.Count > 4096) throw new InvalidOperationException("Water dependency inventory exceeds 4096 bodies.");
            pending.Sort((a, b) => string.CompareOrdinal(a.StableWaterBodyId, b.StableWaterBodyId));
            var invalid = new HashSet<UCWaterBody>();
            foreach (var body in pending)
            {
                string id = body.StableWaterBodyId;
                if (ids.TryGetValue(id, out var duplicate)) { invalid.Add(body); invalid.Add(duplicate); }
                else ids.Add(id, body);
            }
            var dependencies = new Dictionary<UCWaterBody, HashSet<UCWaterBody>>();
            foreach (var body in pending)
            {
                var needs = new HashSet<UCWaterBody>(); dependencies[body] = needs;
                var nodes = body.OwnedRiverNodes(); body.ResolveMouthControl(nodes);
                if (body.joinsRiver != null) Add(body.joinsRiver);
                if (nodes.Length > 0 && nodes[0].connectJunction) Add(nodes[0].junctionTarget);
                foreach (var record in body.waterJunctions)
                {
                    string targetId = record.AtSource ? record.FromBodyId : record.ToBodyId;
                    if (!ids.TryGetValue(targetId, out var target)) target = body.SavedJunctionTarget(record.AtSource);
                    if (target == null) { invalid.Add(body); continue; } // UC-T04-07: a deleted saved identity cannot silently bind to a new nearby body.
                    Add(target);
                }
                if (body.shape == ShapeMode.RiverSpline && nodes.Length > 1)
                {
                    if (body.TryFindLevelBodyAt(nodes[0].transform.position, 2, out var source, out _)) Add(source);
                    if (body.TryFindLevelBodyAt(nodes[nodes.Length - 1].transform.position, 2, out var receiver, out _)) Add(receiver);
                    if (body.joinsRiver == null && receiver == null)
                    {
                        UCWaterBody planned = null;
                        body.MouthSection(nodes[nodes.Length - 1].transform.position, out var left, out var right, out float mouthWidth);
                        foreach (var candidate in pending)
                        {
                            if (!body.SameJunctionScope(candidate) || !TouchesPlannedRiver(candidate, nodes[nodes.Length - 1].transform.position, left, right, mouthWidth)) continue;
                            if (planned != null) { invalid.Add(body); planned = null; break; }
                            planned = candidate;
                        }
                        if (planned != null) Add(planned);
                    }
                }
                void Add(UCWaterBody target)
                {
                    if (!body.SameJunctionScope(target) || !target.enabled) { invalid.Add(body); return; }
                    if (supplied.Contains(target)) needs.Add(target);
                    else if (target.lastBuild == null) invalid.Add(body);
                }
            }
            dependencyMap = dependencies;
            var dependents = new Dictionary<UCWaterBody, List<UCWaterBody>>();
            var degree = new Dictionary<UCWaterBody, int>();
            foreach (var body in pending) { dependents[body] = new List<UCWaterBody>(); degree[body] = dependencies[body].Count; }
            foreach (var body in pending) foreach (var need in dependencies[body]) dependents[need].Add(body);
            var rejected = new Queue<UCWaterBody>(invalid);
            while (rejected.Count > 0)
            {
                var body = rejected.Dequeue();
                foreach (var dependent in dependents[body]) if (invalid.Add(dependent)) rejected.Enqueue(dependent);
            }
            var ready = new SortedSet<UCWaterBody>(Comparer<UCWaterBody>.Create((a, b) => string.CompareOrdinal(a.StableWaterBodyId, b.StableWaterBodyId)));
            foreach (var body in pending) if (!invalid.Contains(body) && degree[body] == 0) ready.Add(body);
            var ordered = new List<UCWaterBody>(); var complete = new HashSet<UCWaterBody>();
            while (ready.Count > 0)
            {
                var body = ready.Min; ready.Remove(body); ordered.Add(body); complete.Add(body);
                foreach (var dependent in dependents[body]) if (--degree[dependent] == 0 && !invalid.Contains(dependent)) ready.Add(dependent);
            }
            foreach (var body in pending) if (!complete.Contains(body)) invalid.Add(body);
            foreach (var body in invalid)
            {
                body.ClearWaterJunctions(true); body.Clear();
                RiverCourseInputNode.UnpublishBodyCourse(body.GetEntityId().GetHashCode());
            }
            // UC-T04-07: retirement can update dependent diagnostics; write the final cause only after the whole invalid component retires.
            foreach (var body in invalid) body.WaterJunctionDiagnostic = "Water dependency has a missing/foreign target, duplicated identity or cycle.";
            return ordered.ToArray();
        }

        private void RebuildWithJunctionDependencies()
        {
            var scope = new List<UCWaterBody>(ScopedJunctionBodies()) { this };
            var order = CreateWaterRebuildOrder(scope.ToArray(), out var dependencies);
            var selected = new HashSet<UCWaterBody> { this };
            // UC-T04-07: refresh dependents of the edited receiver and missing prerequisites,
            // without re-solving unrelated bodies. The finite closure never recurses.
            var dependents = new Dictionary<UCWaterBody, List<UCWaterBody>>();
            foreach (var body in scope) dependents[body] = new List<UCWaterBody>();
            foreach (var pair in dependencies) foreach (var need in pair.Value) dependents[need].Add(pair.Key);
            var queue = new Queue<UCWaterBody>(); queue.Enqueue(this);
            while (queue.Count > 0)
            {
                var body = queue.Dequeue();
                foreach (var dependent in dependents[body]) if (selected.Add(dependent)) queue.Enqueue(dependent);
                if (dependencies.TryGetValue(body, out var needs))
                    foreach (var need in needs) if (selected.Add(need)) queue.Enqueue(need); // UC-T04-09: edited junctions also reprepare receiver wave-contact attenuation.
            }
            foreach (var body in order)
                if (selected.Contains(body)) body.RebuildOrderedWaterBody();
        }

        private bool ApplyJunctionWaveContacts(WaterBodyData data)
        {
            // UC-T04-09: keep the small receiver triangle patch at each planned
            // endpoint still. Independent materials cannot displace one shared edge
            // differently; normal/flow detail remains active over this quiet patch.
            int checks = 0;
            foreach (var river in ScopedJunctionBodies())
            {
                if (river.shape != ShapeMode.RiverSpline) continue;
                var nodes = river.OwnedRiverNodes();
                if (nodes.Length < 2) continue;
                foreach (int endpoint in new[] { 0, nodes.Length - 1 })
                {
                    Vector3 point = nodes[endpoint].transform.position;
                    float radius = Mathf.Max(2, nodes[endpoint].halfWidthM * 2);
                    if (point.x < data.worldBounds.min.x - radius || point.x > data.worldBounds.max.x + radius ||
                        point.z < data.worldBounds.min.z - radius || point.z > data.worldBounds.max.z + radius) continue;
                    for (int i = 0; i < data.triangles.Length; i += 3)
                    {
                        if (++checks > 2097152) { WaterJunctionDiagnostic = "Junction wave-contact preparation exceeds its work budget."; return false; }
                        int ia = data.triangles[i], ib = data.triangles[i + 1], ic = data.triangles[i + 2];
                        Vector3 a = transform.TransformPoint(data.vertices[ia]), b = transform.TransformPoint(data.vertices[ib]), c = transform.TransformPoint(data.vertices[ic]);
                        if (point.x < Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - radius || point.x > Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + radius ||
                            point.z < Mathf.Min(a.z, Mathf.Min(b.z, c.z)) - radius || point.z > Mathf.Max(a.z, Mathf.Max(b.z, c.z)) + radius) continue;
                        data.uv[ia].x = data.uv[ib].x = data.uv[ic].x = -1; // UC-T04-09: suppress geometry motion independently of visible shoreline alpha.
                    }
                }
            }
            return true;
        }

        private void CaptureWaterJunctions()
        {
            waterJunctions.Clear();
            if (carve?.courseWorld == null || carve.courseWorld.Count < 2) return;
            int last = carve.courseWorld.Count - 1;
            var nodes = OwnedRiverNodes();
            UCWaterBody source = nodes.Length > 0 && nodes[0].connectJunction ? nodes[0].junctionTarget : sourceBody;
            UCWaterBody receiver = joinsRiver != null ? joinsRiver : mouthBody;
            if (source != null) Capture(source, true);
            if (receiver != null) Capture(receiver, false);
            void Capture(UCWaterBody other, bool atStart)
            {
                if (atStart && other.kind == WaterBodyKind.Ocean)
                { WaterJunctionDiagnostic = "An ocean cannot supply a directed freshwater river source."; return; }
                if (!SameJunctionScope(other) || other.StableWaterBodyId == StableWaterBodyId)
                { WaterJunctionDiagnostic = "Junction has a self, foreign-world or duplicate body identity."; return; }
                int index = atStart ? 0 : last;
                Vector3 p = carve.courseWorld[index];
                float level = other.IsLevelBody ? other.SurfaceWorldY() : carve.riverBedY[index] + carve.riverDepthM[index];
                var fit = riverSurfaceFit ?? carve;
                int fitIndex = atStart ? 0 : fit.courseWorld.Count - 1; // UC-T04-06: shoreline subdivision can add interior surface stations.
                float width = fit.riverHalfWidth[fitIndex], depth = fit.riverDepthM[fitIndex];
                Vector3 d = atStart ? carve.courseWorld[1] - p : p - carve.courseWorld[last - 1];
                if (!RiverChannelSnapshot.Finite(level) || !RiverChannelSnapshot.Finite(width) || width <= 0 || !RiverChannelSnapshot.Finite(depth) || depth <= 0)
                { WaterJunctionDiagnostic = "Junction has invalid water level or dimensions."; return; }
                WaterJunctionKind kind = atStart ? (other.IsLevelBody ? WaterJunctionKind.LakeToRiver : WaterJunctionKind.RiverToRiver)
                    : other.IsLevelBody ? (other.kind == WaterBodyKind.Ocean ? WaterJunctionKind.RiverToOcean : WaterJunctionKind.RiverToLake) : WaterJunctionKind.RiverToRiver;
                waterJunctions.Add(new WaterJunctionRecord(this, atStart ? other : this, atStart ? this : other, atStart,
                    kind, p, new Vector2(d.x, d.z), width, depth, level, false)); // UC-T04-06 validates shared endpoint sections before accepting geometry.
            }
        }
    }
}
