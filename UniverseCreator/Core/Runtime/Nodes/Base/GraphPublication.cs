using System;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    // UC-T02-07: immutable side results travel with cached fields; evaluation-local effects and scene commit are separate.
    internal abstract class GraphPublication
    {
        internal readonly string Key;
        protected GraphPublication(string key) { Key = key; }
        internal virtual void ReplayEvaluation(TileData tile) { }
    }

    internal sealed class ChannelGraphPublication : GraphPublication
    {
        internal readonly float[] HalfWidth, Depth;
        internal readonly int ResolutionX, ResolutionZ;
        internal readonly float OriginX, OriginZ, CellX, CellZ;
        internal readonly TerrainSystem.RiverOutletTopology Outlets; // UC-T03-06: directed records share the channel product's lifetime.
        internal readonly TerrainSystem.LakeOutletSnapshot LakeOutlets; // UC-T03-07: closed/open source records use the same retained/commit owner.
        internal readonly bool HasChannelCoverage; // UC-T03-07: closed-lake diagnostics are not evidence of a carved channel.
        // UC-T03-09: immutable solve stations retain identity and reference ground for final-terrain reconciliation.
        internal readonly int[] Downstream;
        internal readonly string[] ReachIds;
        internal readonly float[] ReferenceGround;
        internal readonly float ReferenceOriginY, ReferenceHeight;
        internal readonly float[] FixedSurface; // UC-T03-09: named lake source levels remain authoritative at their source station.
        internal ChannelGraphPublication(string key, float[] halfWidth, float[] depth, int resX, int resZ,
            float originX, float originZ, float cellX, float cellZ, TerrainSystem.RiverOutletTopology outlets = null,
            TerrainSystem.LakeOutletSnapshot lakeOutlets = null, int[] downstream = null, string[] reachIds = null,
            float[] referenceGround = null, float referenceOriginY = 0, float referenceHeight = 1, float[] fixedSurface = null) : base(key)
        {
            if (halfWidth == null || depth == null || resX < 2 || resZ < 2 ||
                (long)resX * resZ != halfWidth.LongLength || halfWidth.Length != depth.Length ||
                !Finite(originX) || !Finite(originZ) || !Finite(cellX) || !Finite(cellZ) || cellX <= 0 || cellZ <= 0)
                throw new ArgumentException("Invalid channel publication grid.");
            if ((downstream != null && downstream.Length != halfWidth.Length) ||
                (reachIds != null && reachIds.Length != halfWidth.Length) ||
                (referenceGround != null && referenceGround.Length != halfWidth.Length) ||
                (fixedSurface != null && fixedSurface.Length != halfWidth.Length) ||
                !Finite(referenceOriginY) || !Finite(referenceHeight) || referenceHeight <= 0)
                throw new ArgumentException("Invalid channel station metadata."); // UC-T03-09.
            for (int i = 0; i < halfWidth.Length; i++)
            {
                if (!Finite(halfWidth[i]) || !Finite(depth[i]) || halfWidth[i] < 0 || depth[i] < 0)
                    throw new ArgumentException("Channel publication dimensions must be finite nonnegative metres.");
                if ((referenceGround != null && !Finite(referenceGround[i])) ||
                    (downstream != null && (downstream[i] < -1 || downstream[i] >= halfWidth.Length)) ||
                    (fixedSurface != null && float.IsInfinity(fixedSurface[i])))
                    throw new ArgumentException("Invalid channel reference, receiver or source level."); // UC-T03-09: NaN alone means no fixed source level.
                HasChannelCoverage |= halfWidth[i] > 0 && depth[i] > 0;
            }
            HalfWidth = halfWidth; Depth = depth; ResolutionX = resX; ResolutionZ = resZ;
            OriginX = originX; OriginZ = originZ; CellX = cellX; CellZ = cellZ;
            Outlets = outlets; // UC-T03-06: publication ownership/commit remains with the existing channel owner.
            LakeOutlets = lakeOutlets;
            Downstream = downstream; ReachIds = reachIds; ReferenceGround = referenceGround;
            ReferenceOriginY = referenceOriginY; ReferenceHeight = referenceHeight;
            FixedSurface = fixedSurface;
        }
        internal override void ReplayEvaluation(TileData tile)
        {
            if (LakeOutlets != null) tile.outputs.lakeOutletReports[Key] = LakeOutlets; // UC-T03-07: cold and retained pulls report the same sources.
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    // UC-T06-05: sampler counts travel with the same replay/retirement owner as retained placement products.
    internal sealed class ScatterSamplingGraphPublication : GraphPublication
    {
        private readonly PoissonSamplingReport report;
        internal ScatterSamplingGraphPublication(string key, PoissonSamplingReport report) : base(key) { this.report = report; }
        internal override void ReplayEvaluation(TileData tile) { tile.outputs.scatterSamplingReports[Key] = report; }
    }

    // UC-T06-17: cold/retained evaluations replay identical immutable stage evidence.
    internal sealed class VegetationPlacementGraphPublication : GraphPublication
    {
        private readonly Scatter.VegetationPlacementReport report;
        internal VegetationPlacementGraphPublication(string key, Scatter.VegetationPlacementReport report) : base(key) { this.report = report; }
        internal override void ReplayEvaluation(TileData tile) { tile.outputs.vegetationPlacementReports[Key] = report; }
    }

    // UC-T03-05: achieved/target diagnostics follow cold, local and retained evaluation paths.
    internal sealed class RiverHierarchyGraphPublication : GraphPublication
    {
        private readonly TerrainSystem.RiverHierarchyReport report;
        internal RiverHierarchyGraphPublication(string key, TerrainSystem.RiverHierarchyReport report) : base(key)
        { this.report = report ?? throw new ArgumentNullException(nameof(report)); }
        internal override void ReplayEvaluation(TileData tile) { tile.outputs.riverHierarchyReports[Key] = report; }
    }

    internal readonly struct CanopyGraphStamp
    {
        internal readonly float X, Z, Radius, LeafArea;
        internal readonly Ecology.CanopyLayer Layer;
        internal CanopyGraphStamp(float x, float z, float radius, float leafArea, Ecology.CanopyLayer layer)
        { X = x; Z = z; Radius = radius; LeafArea = leafArea; Layer = layer; }
    }

    // UC-T03-08: eligible/achieved lake selection evidence replays with retained masks.
    internal sealed class LakeDistributionGraphPublication : GraphPublication
    {
        private readonly TerrainSystem.LakeDistributionReport report;
        internal LakeDistributionGraphPublication(string key, TerrainSystem.LakeDistributionReport report) : base(key)
        { this.report = report ?? throw new ArgumentNullException(nameof(report)); }
        internal override void ReplayEvaluation(TileData tile) { tile.outputs.lakeDistributionReports[Key] = report; }
    }

    internal sealed class EcologyGraphPublication : GraphPublication
    {
        private readonly Matrix height;
        private readonly CanopyGraphStamp[] stamps;
        private readonly int[] histogram;
        internal EcologyGraphPublication(string key, Matrix height, CanopyGraphStamp[] stamps, int[] histogram) : base(key)
        { this.height = height; this.stamps = stamps; this.histogram = histogram; }
        internal override void ReplayEvaluation(TileData tile)
        {
            if (tile.ecology != null)
            {
                tile.ecology.EnsureBound(height);
                if (tile.ecology.settings.canopyCompetition) // UC-T08-15: retained evaluation observes the same switch as cold selection.
                    foreach (CanopyGraphStamp stamp in stamps)
                        tile.ecology.canopy?.Deposit(stamp.X, stamp.Z, stamp.Radius, stamp.LeafArea, stamp.Layer);
            }
            for (int i = 0; i < histogram.Length && i < tile.outputs.zoneHistogram.Length; i++)
                tile.outputs.zoneHistogram[i] += histogram[i];
        }
    }

    internal sealed class RetainedGraphPayload
    {
        internal readonly object Product;
        internal readonly GraphPublication[] Publications;
        internal RetainedGraphPayload(object product, GraphPublication[] publications)
        { Product = product; Publications = publications; }
    }
}
