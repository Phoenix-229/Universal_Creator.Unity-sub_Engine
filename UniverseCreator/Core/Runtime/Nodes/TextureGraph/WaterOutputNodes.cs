// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    WaterOutputNodes.cs
//  Purpose: The terminal nodes that turn hydrology maths into water geometry.
//
//  Water Body Output takes a bed, a water level and an optional mask and files
//  finished meshes into the output bag. Ocean Output is the same thing with the
//  level pinned to a single elevation, which is the common case and deserves not
//  to need a Constant node wired into it.
//
//  WHY TWO NODES AND NOT ONE WITH A TICK BOX
//  -----------------------------------------
//  The port lists differ. A tick box that changes which inputs exist means the
//  graph has to drop connections when it is toggled, and a user who unticks it by
//  accident loses wiring they cannot get back with undo in the node UI. Two nodes
//  with fixed ports cost one extra menu entry and nothing else.
//
//  WHY THESE ARE Output CATEGORY AND NOT Water
//  -------------------------------------------
//  Their Bed input is a Matrix, and only the height graph produces those. The
//  Texture Graph window lists every Water-category node, so filing these under
//  Water would offer them in a window where GetInputMatrix always returns null -
//  the node would sit there looking connected and generate nothing. Output keeps
//  them in the height graph, where a bed exists.
//
//  The Water input still works there: Water Surface and the rest of the water
//  material nodes are Water category, and the height graph filters nothing, so
//  they are available in both windows. The material description can therefore
//  either be wired in directly here, or come from the texture graph through
//  Shader Output and UCWaterApplier - whichever suits the project.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Texturing
{
    /// <summary>
    /// Builds water surfaces wherever the water level stands above the bed.
    ///
    /// The Level input is what makes this general: wire Lake Fill's spill
    /// elevation into it for lakes, the river carve's water surface for rivers, or
    /// leave it unconnected and set Sea Level for a flat sea. All three are the
    /// same operation - a level field minus a bed field is a depth field - so they
    /// are one node rather than three that would drift apart.
    /// </summary>
    [System.Serializable]
    public class WaterBodyOutputNode : OutputNode
    {
        public override string NodeName { get { return "Water Body Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }
        public override string Tooltip
        {
            get { return "Builds water surface meshes where the water level stands above the terrain."; }
        }

        private static readonly Port[] Ins =
        {
            new Port("Terrain Height", PortType.Matrix, PortDirection.Input),
            new Port("Water Level", PortType.Matrix, PortDirection.Input),
            new Port("Water Mask",  PortType.Matrix, PortDirection.Input),
            new Port("Water", PortType.Water,  PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.None; }

        [Tooltip("What this is. Sets the mesh name and the physics body kind, and " +
                 "nothing else - the geometry comes from the fields, not from this.")]
        public WaterBodyKind kind = WaterBodyKind.Lake;

        [Tooltip("Used when Level is unconnected, as a fraction of terrain height. " +
                 "0.3 on a 600m terrain is a sea at 180m.")]
        [Range(0f, 1f)]
        public float seaLevel = 0.3f;

        [Tooltip("Raises or lowers the finished surface, in metres. Use small " +
                 "negative values to sink water slightly into its bed and hide a " +
                 "shoreline that reads too sharp.")]
        public float surfaceOffsetMetres = -0.2f;

        [Tooltip("Vertices along the chunk's longest edge. 64 is right for a phone; " +
                 "the wave detail comes from the shader, not from these triangles.")]
        [Range(8, 240)]
        public int meshResolution = 64;

        [Tooltip("Water thinner than this is not water. Raising it removes the " +
                 "one-texel puddles a noisy level field leaves along every contour.")]
        public float minDepthMetres = 0.05f;

        [Tooltip("Bodies smaller than this are dropped, in square metres.")]
        public float minAreaSquareMetres = 4f;

        [Tooltip("Depth over which the surface fades in at the bank. Larger hides " +
                 "the mesh edge better and eats further into shallow water.")]
        public float edgeFeatherMetres = 0.6f;

        [Tooltip("Manning roughness. 0.011 concrete, 0.035 natural stream, 0.07 a " +
                 "boulder field. Higher is rougher, which means slower and whiter.")]
        [Range(0.008f, 0.15f)]
        public float manningRoughness = 0.035f;

        [Tooltip("Caps the hydraulic radius Manning's equation sees. Manning is a " +
                 "channel formula; a 200m lake depth would return a velocity no " +
                 "lake has.")]
        public float hydraulicRadiusCap = 3f;

        [Tooltip("Flow speed that fills the vertex flow channel, in m/s. The " +
                 "material's Mesh Flow Scale is set to this so the shader scrolls " +
                 "at the real speed.")]
        public float maxFlowSpeed = 4f;

        [Tooltip("Adds a UC Water Volume to each body, so swimmers, boats and " +
                 "ripples know where the water is. Off means visuals only.")]
        public bool generatePhysicsVolume = true;

        [Tooltip("Layer for the generated water objects, or -1 to use the world's " +
                 "Water Layer. Set it here only when this one body needs a different " +
                 "layer from the rest of the world's water.")]
        public int layer = -1;

        protected override void WriteOutput(Matrix src, TileData tile, StopToken stop)
        {
            // Unused: the four-port override means Generate is replaced below and
            // this never runs. Present because OutputNode declares it abstract.
        }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix bed = GetInputMatrix(tile, 0, stop);
            if (bed == null) return null;
            if (stop != null && stop.StopRequested) return null;

            Matrix level = GetInputMatrix(tile, 1, stop);
            Matrix mask = GetInputMatrix(tile, 2, stop);
            WaterProduct product = tile.PullInput(this, 3, stop) as WaterProduct;

            if (stop != null && stop.StopRequested) return null;

            WaterBuildSettings settings = BuildSettings(this);

            List<WaterBodyData> bodies = new List<WaterBodyData>();
            WaterMeshBuilder.Build(bed, level, seaLevel, mask, settings, bodies, stop);

            if (stop != null && stop.StopRequested) return null;

            for (int i = 0; i < bodies.Count; i++)
            {
                bodies[i].product = product;
                bodies[i].generateVolume = generatePhysicsVolume;
                bodies[i].layerOverride = layer;
                tile.outputs.waterBodies.Add(bodies[i]);
            }

            return null;
        }

        internal static WaterBuildSettings BuildSettings(WaterBodyOutputNode n)
        {
            WaterBuildSettings s = WaterBuildSettings.Default(n.kind);
            s.name = n.kind.ToString();
            s.meshResolution = n.meshResolution;
            s.minDepthMetres = Mathf.Max(0.001f, n.minDepthMetres);
            s.minAreaSquareMetres = Mathf.Max(0f, n.minAreaSquareMetres);
            s.edgeFeatherMetres = Mathf.Max(0.01f, n.edgeFeatherMetres);
            s.manningRoughness = n.manningRoughness;
            s.hydraulicRadiusCap = Mathf.Max(0.05f, n.hydraulicRadiusCap);
            s.maxFlowSpeed = Mathf.Max(0.01f, n.maxFlowSpeed);
            s.surfaceOffsetMetres = n.surfaceOffsetMetres;
            return s;
        }
    }

    /// <summary>
    /// A sea at one elevation across the whole world.
    ///
    /// Separate from Water Body Output because an ocean is the one water body whose
    /// surface is genuinely constant, and because it is the one that must not be
    /// clipped to what has been generated: the sea is visible past the last chunk,
    /// and a per-chunk ocean ends in mid-air at the edge of the loaded world. That
    /// is handled by UCWaterApplier building one shared far-field plane rather than
    /// by this node emitting anything different - it still emits per-chunk geometry
    /// so that the shoreline is exact where the terrain exists.
    /// </summary>
    [System.Serializable]
    public class OceanOutputNode : OutputNode
    {
        public override string NodeName { get { return "Ocean Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }
        public override string Tooltip
        {
            get { return "Floods everything below sea level, and extends the sea past the generated world."; }
        }

        private static readonly Port[] Ins =
        {
            new Port("Bed",   PortType.Matrix, PortDirection.Input),
            new Port("Mask",  PortType.Matrix, PortDirection.Input),
            new Port("Water", PortType.Water,  PortDirection.Input)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.None; }

        [Tooltip("Sea level as a fraction of terrain height.")]
        [Range(0f, 1f)]
        public float seaLevel = 0.3f;

        [Tooltip("Vertices along the chunk's longest edge.")]
        [Range(8, 240)]
        public int meshResolution = 48;

        [Tooltip("Depth over which the sea fades in at the beach.")]
        public float edgeFeatherMetres = 1.2f;

        [Tooltip("Raises or lowers the finished surface, in metres. Use small " +
                 "negative values to sink water slightly into its bed and hide a " +
                 "shoreline that reads too sharp.")]
        public float surfaceOffsetMetres = -0.2f;

        [Tooltip("Extends a flat sea this far past the generated terrain, in metres, " +
                 "so the horizon is water rather than the edge of the world. 0 to " +
                 "switch it off.")]
        public float horizonExtentMetres = 4000f;

        [Tooltip("Adds a UC Water Volume to the sea.")]
        public bool generatePhysicsVolume = true;

        [Tooltip("Layer for the generated sea, or -1 to use the world's Water Layer.")]
        public int layer = -1;

        protected override void WriteOutput(Matrix src, TileData tile, StopToken stop) { }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix bed = GetInputMatrix(tile, 0, stop);
            if (bed == null) return null;
            if (stop != null && stop.StopRequested) return null;

            Matrix mask = GetInputMatrix(tile, 1, stop);
            WaterProduct product = tile.PullInput(this, 2, stop) as WaterProduct;

            if (stop != null && stop.StopRequested) return null;

            WaterBuildSettings settings = WaterBuildSettings.Default(WaterBodyKind.Ocean);
            settings.name = "Ocean";
            settings.meshResolution = meshResolution;
            settings.edgeFeatherMetres = Mathf.Max(0.01f, edgeFeatherMetres);
            settings.surfaceOffsetMetres = surfaceOffsetMetres;

            // A sea surface is level, so its slope is zero, so Manning returns zero
            // and the ocean does not flow. That is correct - ocean movement is swell
            // and tide, which the shader's wave and the material's uniform flow
            // handle - but the settings are still filled in so a user who wires a
            // sloped level field in gets sensible numbers rather than zeros.
            settings.minAreaSquareMetres = Mathf.Max(settings.minAreaSquareMetres, 16f);

            List<WaterBodyData> bodies = new List<WaterBodyData>();
            WaterMeshBuilder.Build(bed, null, seaLevel, mask, settings, bodies, stop);

            if (stop != null && stop.StopRequested) return null;

            for (int i = 0; i < bodies.Count; i++)
            {
                bodies[i].product = product;
                bodies[i].generateVolume = generatePhysicsVolume;
                bodies[i].layerOverride = layer;

                // Only the ocean carries a horizon, and only one body needs to: the
                // applier builds a single shared plane, so setting it on every body
                // in a multi-chunk sea would ask for the same plane a hundred times.
                bodies[i].horizonExtentMetres = horizonExtentMetres;

                tile.outputs.waterBodies.Add(bodies[i]);
            }

            return null;
        }
    }
}
