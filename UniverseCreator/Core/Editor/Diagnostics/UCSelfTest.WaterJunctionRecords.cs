// UC-T04-05: directed owned junction identities survive serialization and reject overlapping foreign worlds.
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestWaterJunctionRecords()
        {
            var root = new GameObject("UC persistent water junctions"); root.SetActive(false);
            var otherRoot = new GameObject("UC foreign junction world"); otherRoot.SetActive(false);
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.maxHeightMeters = 100;
                otherRoot.AddComponent<UniverseCreatorObject>();
                var lake = JunctionLevelBody(root.transform, "Source lake", new Vector3(0, 49, 0), WaterBodyKind.Lake);
                var ocean = JunctionLevelBody(root.transform, "Ocean", new Vector3(0, 45, 40), WaterBodyKind.Ocean);
                var river = JunctionRiverBody(root.transform, "Lake outlet", new Vector3(0, 50, 0), new Vector3(0, 46, 40));
                river.BuildDig(false); var records = river.GetWaterJunctionRecords();
                Check(records.Length == 2 && records[0].Kind == WaterJunctionKind.LakeToRiver && records[1].Kind == WaterJunctionKind.RiverToOcean &&
                    records[0].FromBodyId == lake.StableWaterBodyId && records[0].ToBodyId == river.StableWaterBodyId &&
                    records[1].FromBodyId == river.StableWaterBodyId && records[1].ToBodyId == ocean.StableWaterBodyId &&
                    records[0].WorldOwner == world && records[0].Revision == world.GenerationScheduler.CurrentRun.Revision,
                    "UC-T04-05 lake sources and ocean receivers produce separately owned directed persistent records.");
                if (records.Length == 2)
                {
                    var copied = JsonUtility.FromJson<WaterJunctionRecord>(JsonUtility.ToJson(records[1]));
                    Check(copied.JunctionId == records[1].JunctionId && copied.FromReachId == records[1].FromReachId &&
                        copied.ToReachId == records[1].ToReachId && copied.Position == records[1].Position && copied.DirectionXZ == records[1].DirectionXZ &&
                        copied.HalfWidthMetres > 0 && copied.DepthMetres > 0 && !copied.GeometryAccepted && !ReferenceEquals(records[1], river.GetWaterJunctionRecords()[1]),
                        "UC-T04-05 serialization retains identities and metric dimensions; copied candidate records cannot claim accepted geometry.");
                }
                ocean.transform.localPosition = new Vector3(0, 45, 200); // Kind is creation-locked; replace the receiver instead of trying to re-kind it.
                JunctionLevelBody(root.transform, "Receiving lake", new Vector3(0, 45, 40), WaterBodyKind.Lake);
                river.InvalidatePendingWaterAuthoring(); river.BuildDig(false); // UC-T04-07: explicit target edits retire saved candidates through the production editor boundary.
                Check(river.GetWaterJunctionRecords().Length == 2 && river.GetWaterJunctionRecords()[1].Kind == WaterJunctionKind.RiverToLake,
                    "UC-T04-05 fresh standing-water receivers are distinguished from ocean outlets.");
                var main = JunctionRiverBody(root.transform, "Receiving river", new Vector3(10, 48, 30), new Vector3(10, 46, 60)); main.BuildDig(false);
                river.joinsRiver = main; river.InvalidatePendingWaterAuthoring(); river.BuildDig(false); records = river.GetWaterJunctionRecords();
                Check(records.Length == 2 && records[1].Kind == WaterJunctionKind.RiverToRiver && records[1].ToBodyId == main.StableWaterBodyId,
                    "UC-T04-05 a tributary owns its receiver reach independently of its lake-source record. Surface: " + river.RiverSurfaceDiagnostic);
                var tributary = JunctionRiverBody(root.transform, "Second tributary", new Vector3(20, 49, 30), new Vector3(10, 46, 50));
                tributary.joinsRiver = main; tributary.BuildDig(false);
                var second = tributary.GetWaterJunctionRecords();
                Check(second.Length > 0 && records.Length > 0 && second[second.Length - 1].ToBodyId == main.StableWaterBodyId &&
                    second[second.Length - 1].JunctionId != records[records.Length - 1].JunctionId,
                    "UC-T04-05 multiple tributaries have distinct owners and do not overwrite a receiver's other links.");
                main.transform.SetParent(otherRoot.transform, false); river.BuildDig(false);
                Check(river.WaterJunctionDiagnostic.Length > 0 && System.Array.TrueForAll(river.GetWaterJunctionRecords(), r => r.ToBodyId != main.StableWaterBodyId),
                    "UC-T04-05 an overlapping receiver moved to another world is rejected without a cross-world route extension.");
            }
            finally
            {
                foreach (var parent in new[] { root, otherRoot }) foreach (var body in parent.GetComponentsInChildren<UCWaterBody>(true))
                { RiverCourseInputNode.UnpublishBodyCourse(body.GetEntityId().GetHashCode()); WaterCarveRegistry.Unregister(body); body.Clear(); }
                Object.DestroyImmediate(root); Object.DestroyImmediate(otherRoot);
            }
        }

        private static UCWaterBody JunctionLevelBody(Transform owner, string name, Vector3 position, WaterBodyKind kind)
        {
            var root = new GameObject(name); root.transform.SetParent(owner, false); root.transform.localPosition = position;
            var body = root.AddComponent<UCWaterBody>(); body.SetKindOnce(kind); body.shape = UCWaterBody.ShapeMode.Rectangle;
            body.sizeMetres = new Vector2(12, 12); return body;
        }

        private static UCWaterBody JunctionRiverBody(Transform owner, string name, Vector3 first, Vector3 last)
        {
            var root = new GameObject(name); root.transform.SetParent(owner, false);
            var body = root.AddComponent<UCWaterBody>(); body.SetKindOnce(WaterBodyKind.River); body.shape = UCWaterBody.ShapeMode.RiverSpline;
            body.meander = 0; body.ruggedness = 0; body.corridorStrength = 0;
            var a = RiverModePoint(root.transform, first, false); var b = RiverModePoint(root.transform, last, false);
            a.halfWidthM = b.halfWidthM = 1; a.depthM = b.depthM = 2; body.EnsureRiverTerrainMode(); return body;
        }
    }
}
