// UC-T06-17: immutable stage evidence follows the existing cache/publication lifetime, with no per-candidate logging.
using System;

namespace UniverseCreator.Nodes.Scatter
{
    /// <summary>Observed counts for one completed placement stage; point/cell counts remain distinct from final rendered instances.</summary>
    /// <remarks>One worker owns construction. Read after output acceptance; cancellation publishes no completed-stage claim.</remarks>
    public readonly struct VegetationPlacementReport
    {
        /// <summary>Named production stage and the units of its input/output counts.</summary>
        public readonly string Stage, Units;
        /// <summary>Count entering and leaving this stage; decorators/detail grids may increase output.</summary>
        public readonly long Inputs, Outputs;
        /// <summary>Requested rectangle area in square metres.</summary>
        public readonly double AreaSquareMetres;
        /// <summary>Explicit dependency/conversion guidance; empty for a count-only stage.</summary>
        public readonly string Guidance;
        internal VegetationPlacementReport(string stage, long inputs, long outputs, double area, string units, string guidance)
        { Stage = stage; Inputs = inputs; Outputs = outputs; AreaSquareMetres = area; Units = units; Guidance = guidance ?? ""; }
        internal static void Publish(TileData tile, string stage, long inputs, long outputs, string units = "instances", string guidance = "")
        {
            if (tile == null || inputs < 0 || outputs < 0) throw new ArgumentException("Placement reports require a tile and nonnegative observed counts.");
            var report = new VegetationPlacementReport(stage, inputs, outputs, (double)tile.worldSize.x * tile.worldSize.z, units, guidance);
            tile.AddPublication(new VegetationPlacementGraphPublication(tile.CurrentPublicationKey + ":placement:" + stage, report));
        }
    }
}
