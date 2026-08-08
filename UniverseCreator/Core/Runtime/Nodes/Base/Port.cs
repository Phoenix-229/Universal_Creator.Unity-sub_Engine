// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    Port.cs
//  Purpose: Port metadata and type constraints for the pull-based node graph.
//           Each Generator subclass declares static Port[] arrays that describe
//           what it can accept and produce.
// -----------------------------------------------------------------------------

using System;

namespace UniverseCreator.Nodes
{
    /// <summary>Port direction: inputs are pulled, outputs are evaluated on demand.</summary>
    public enum PortDirection
    {
        Input = 0,
        Output = 1
    }

    /// <summary>
    /// Data type carried by a port. Module 1 has only Matrix; later modules
    /// add MeshData, TextureData, etc.
    /// </summary>
    public enum PortType
    {
        Matrix = 0,
        // Future: MeshData, TextureData, SplineData, etc.
    }

    /// <summary>
    /// Static metadata for one port on a Generator. The Generator subclass declares
    /// arrays of these (GetInputs() / GetOutputs()) so the graph editor can draw
    /// connections and the evaluator can type-check them.
    /// </summary>
    [Serializable]
    public struct Port
    {
        public string name;
        public PortType type;
        public PortDirection direction;

        /// <summary>
        /// True if the port accepts multiple incoming connections. For Module 1,
        /// inputs are single-connection (last write wins), outputs can fan out.
        /// </summary>
        public bool allowMultiple;

        public Port(string name, PortType type, PortDirection direction, bool allowMultiple = false)
        {
            this.name = name;
            this.type = type;
            this.direction = direction;
            this.allowMultiple = allowMultiple;
        }

        public override string ToString()
        {
            return direction + " " + name + " (" + type + ")";
        }
    }

    /// <summary>
    /// Cached port arrays shared by node categories. GetInputs/GetOutputs are called
    /// every editor repaint, so they must not allocate.
    /// </summary>
    public static class SharedPorts
    {
        public static readonly Port[] None = new Port[0];

        public static readonly Port[] HeightIn =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input)
        };

        public static readonly Port[] HeightOut =
        {
            new Port("Height", PortType.Matrix, PortDirection.Output, true)
        };

        public static readonly Port[] MaskOut =
        {
            new Port("Mask", PortType.Matrix, PortDirection.Output, true)
        };

        public static readonly Port[] HeightMaskIn =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Mask",   PortType.Matrix, PortDirection.Input)
        };

        public static readonly Port[] TwoHeightIn =
        {
            new Port("A", PortType.Matrix, PortDirection.Input),
            new Port("B", PortType.Matrix, PortDirection.Input)
        };

        public static readonly Port[] TwoHeightMaskIn =
        {
            new Port("A",    PortType.Matrix, PortDirection.Input),
            new Port("B",    PortType.Matrix, PortDirection.Input),
            new Port("Mask", PortType.Matrix, PortDirection.Input)
        };
    }
}
