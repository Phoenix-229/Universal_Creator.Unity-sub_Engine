// UC-T04-05: persistent directed scene-water connections, separate from runtime channel-grid ownership.
using System;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>Directed water-body connection; lake includes other still fresh-water bodies.</summary>
    public enum WaterJunctionKind
    {
        /// <summary>A tributary enters a river, or a river supplies a branch.</summary>
        RiverToRiver,
        /// <summary>A river supplies standing fresh water.</summary>
        RiverToLake,
        /// <summary>Standing fresh water supplies a river.</summary>
        LakeToRiver,
        /// <summary>A river supplies the ocean.</summary>
        RiverToOcean
    }

    /// <summary>Serialized, river-owned endpoint identity and metric geometry; consumers receive copies.</summary>
    /// <remarks>Body/reach/junction IDs survive scene saves. Runtime world identity and revision must
    /// be reconciled after reload; a saved candidate is never proof of current accepted geometry.</remarks>
    [Serializable]
    public sealed class WaterJunctionRecord
    {
        [SerializeField] private string junctionId, ownerBodyId, fromBodyId, toBodyId, fromReachId, toReachId;
        [SerializeField] private UniverseCreatorObject worldOwner;
        [SerializeField] private string worldLifetimeId;
        [SerializeField] private long revision;
        [SerializeField] private WaterJunctionKind kind;
        [SerializeField] private Vector3 position;
        [SerializeField] private Vector2 direction;
        [SerializeField] private float halfWidthMetres, depthMetres, surfaceLevelMetres;
        [SerializeField] private bool atSource, geometryAccepted;

        /// <summary>Persistent owner/endpoint ID; changing receiver retires the previous record at this ID.</summary>
        public string JunctionId => junctionId;
        /// <summary>River responsible for this record, including a lake-source connection.</summary>
        public string OwnerBodyId => ownerBodyId;
        /// <summary>Persistent supplying body ID.</summary>
        public string FromBodyId => fromBodyId;
        /// <summary>Persistent receiving body ID.</summary>
        public string ToBodyId => toBodyId;
        /// <summary>Supplying reach ID, derived from body identity and shape.</summary>
        public string FromReachId => fromReachId;
        /// <summary>Receiving reach ID, derived from body identity and shape.</summary>
        public string ToReachId => toReachId;
        /// <summary>Serialized owning world reference; null for standalone scene water.</summary>
        public UniverseCreatorObject WorldOwner => worldOwner;
        /// <summary>Runtime world-lifetime GUID string; empty for standalone scene water.</summary>
        public string WorldLifetimeId => worldLifetimeId;
        /// <summary>Captured positive runtime generation revision, or zero for standalone water.</summary>
        public long Revision => revision;
        /// <summary>Supplying/receiving body relationship.</summary>
        public WaterJunctionKind Kind => kind;
        /// <summary>World-metre connection centre at the shared water level.</summary>
        public Vector3 Position => position;
        /// <summary>Unit XZ flow direction at this endpoint.</summary>
        public Vector2 DirectionXZ => direction;
        /// <summary>Endpoint half-width in metres; finite and positive.</summary>
        public float HalfWidthMetres => halfWidthMetres;
        /// <summary>Endpoint water depth in metres; finite and positive.</summary>
        public float DepthMetres => depthMetres;
        /// <summary>Shared water surface elevation in world metres.</summary>
        public float SurfaceLevelMetres => surfaceLevelMetres;
        /// <summary>True when this record belongs to its owner's source endpoint.</summary>
        public bool AtSource => atSource;
        /// <summary>True only after current endpoint geometry and ground coverage have validated.</summary>
        public bool GeometryAccepted => geometryAccepted;

        internal WaterJunctionRecord(UCWaterBody owner, UCWaterBody from, UCWaterBody to, bool source,
            WaterJunctionKind connection, Vector3 centre, Vector2 flow, float width, float depth, float level, bool accepted)
        {
            ownerBodyId = owner.StableWaterBodyId; junctionId = ownerBodyId + (source ? ":source" : ":mouth");
            fromBodyId = from.StableWaterBodyId; toBodyId = to.StableWaterBodyId;
            fromReachId = fromBodyId + (from.IsLevelBody ? ":level" : ":river");
            toReachId = toBodyId + (to.IsLevelBody ? ":level" : ":river");
            worldOwner = owner.CarveWorld; GenerationRunId run = worldOwner != null ? worldOwner.GenerationScheduler.CurrentRun : default;
            worldLifetimeId = run.IsAssigned ? run.WorldId.ToString("N") : string.Empty; revision = run.Revision;
            kind = connection; atSource = source; centre.y = level; position = centre; direction = flow.normalized;
            halfWidthMetres = width; depthMetres = depth; surfaceLevelMetres = level; geometryAccepted = accepted;
        }

        internal WaterJunctionRecord Copy() => (WaterJunctionRecord)MemberwiseClone();

        // UC-T04-06: only the native surface publication owner can promote a prepared candidate.
        internal WaterJunctionRecord AcceptedCopy()
        {
            var accepted = Copy(); accepted.geometryAccepted = true; return accepted;
        }

        // UC-T04-07: callers never receive serialized acceptance without live runtime witnesses.
        internal WaterJunctionRecord CopyWithGeometry(bool accepted)
        {
            var record = Copy(); record.geometryAccepted = accepted; return record;
        }
    }
}
