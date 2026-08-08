// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    GrassDetailScatterNode.cs
//  Purpose: STUB. Fills Unity detail layers with grass/undergrowth coverage.
//           Wiring is final; the coverage solver lands in Module 3 (Vegetation).
//
//  Grass differs from the other scatter types: Unity stores it as a per-patch
//  density integer grid rather than discrete transforms, so this node writes
//  OutputBag.detailLayers instead of the scatter channel.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public class GrassDetailScatterNode : ScatterNodeBase
    {
        public override string NodeName { get { return "Grass Detail"; } }
        public override string Tooltip { get { return "STUB (Module 3). Fills a Unity detail layer."; } }
        public override string Channel { get { return "grass"; } }

        /// <summary>Index into the terrain's detail prototype array.</summary>
        public int detailLayerIndex;

        /// <summary>Maximum per-cell density written into the detail grid.</summary>
        public int maxDensityPerCell = 8;

        protected override void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop)
        {
            // TODO [Module 3]: grass does not use discrete instances. Resample the
            // mask to the terrain's detailResolution, scale by maxDensityPerCell,
            // and write the resulting int[,] to
            //     tile.outputs.detailLayers[detailLayerIndex]
            // rather than populating the instance list. The base class still files
            // an empty scatter channel so downstream consumers see a consistent key.
        }
    }
}
