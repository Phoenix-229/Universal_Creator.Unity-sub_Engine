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
    /// Data type carried by a port. Module 1 shipped only Matrix; Module 2's
    /// texture graph adds the material-authoring currencies.
    ///
    /// WHY THESE AND NOT "JUST RGBA"
    /// -----------------------------
    /// The terrain is drawn by a fixed uber-shader, so the texture graph is not
    /// building a pixel program - it is describing a material. What flows down a
    /// wire is therefore a description (this layer, that gradient, this much
    /// snow), not a colour sampled at a pixel. Typing those descriptions
    /// separately is what lets the editor refuse a wire that could never mean
    /// anything, instead of accepting it and producing a silently wrong material.
    ///
    /// Existing values keep their numbers. PortType is serialized inside Port,
    /// which is serialized inside every node's port arrays, so renumbering would
    /// retype every connection in every graph saved before the change.
    /// </summary>
    public enum PortType
    {
        /// <summary>A float per pixel: heightfield, mask, or baked map.</summary>
        Matrix = 0,

        /// <summary>One terrain material layer - albedo, normal, mask, tiling, PBR.</summary>
        Layer = 1,

        /// <summary>Up to four Layers with the control-map channel each occupies.</summary>
        Splat = 2,

        /// <summary>A single RGBA constant.</summary>
        Color = 3,

        /// <summary>A single float constant.</summary>
        Scalar = 4,

        /// <summary>A multi-stop colour ramp, baked to a 256x1 LUT on output.</summary>
        Gradient = 5,

        /// <summary>The accumulated surface description that Shader Output applies.</summary>
        Surface = 6,

        /// <summary>A texture reference plus its import-time sampling intent.</summary>
        Texture = 7,

        /// <summary>A UV transform: tiling, offset, rotation.</summary>
        UV = 8,

        /// <summary>A water surface description, consumed by the water output.</summary>
        Water = 9,

        /// <summary>UC-T07-02: immutable CPU float RGBA pixels with explicit encoding, channels and dimensions.</summary>
        Image = 10,
        /// <summary>UC-T07-03: one finite four-component shader parameter, independent of colour encoding.</summary>
        Vector = 11,
        /// <summary>UC-T08-03: immutable early temperature/moisture/prevailing-wind and seasonal/regional climate recipe.</summary>
        Climate = 12,
        /// <summary>UC-T08-06: immutable bounded local weather overrides; consumed by this world's existing weather manager.</summary>
        Weather = 13
    }

    /// <summary>
    /// How a multi-input (fan-in) port folds its several incoming products into
    /// one. Only meaningful on an INPUT port whose <see cref="Port.allowMultiple"/>
    /// is true; single-connection ports ignore it. Every operation here is
    /// commutative on purpose: <see cref="Graph.GetInputConnections"/> returns
    /// edges in list order, which is not stable across a graph re-save, so a
    /// fan-in must never depend on the order its wires happen to sit in.
    /// </summary>
    public enum MergeOp
    {
        /// <summary>Per-pixel maximum - the union of several shapes (the default).</summary>
        Max = 0,

        /// <summary>Per-pixel minimum - intersection / lowest-wins.</summary>
        Min = 1,

        /// <summary>Per-pixel arithmetic mean of every connected input.</summary>
        Average = 2,

        /// <summary>Per-pixel sum of every connected input.</summary>
        Sum = 3
    }

    /// <summary>
    /// Wire and port colours per data type. Distinct hues are the only thing that
    /// makes a graph with ten currencies readable at a glance - by the time the
    /// user has to click a port to learn what it carries, the graph has stopped
    /// being a diagram and become a form.
    /// </summary>
    public static class PortTypeColors
    {
        public static UnityEngine.Color Get(PortType type)
        {
            switch (type)
            {
                case PortType.Matrix:   return new UnityEngine.Color(0.62f, 0.68f, 0.74f); // pale slate
                case PortType.Layer:    return new UnityEngine.Color(0.85f, 0.62f, 0.30f); // ochre
                case PortType.Splat:    return new UnityEngine.Color(0.90f, 0.45f, 0.35f); // terracotta
                case PortType.Color:    return new UnityEngine.Color(0.95f, 0.80f, 0.35f); // gold
                case PortType.Scalar:   return new UnityEngine.Color(0.55f, 0.80f, 0.55f); // sage
                case PortType.Gradient: return new UnityEngine.Color(0.80f, 0.50f, 0.85f); // orchid
                case PortType.Surface:  return new UnityEngine.Color(0.45f, 0.75f, 0.95f); // sky
                case PortType.Texture:  return new UnityEngine.Color(0.70f, 0.60f, 0.95f); // periwinkle
                case PortType.UV:       return new UnityEngine.Color(0.50f, 0.85f, 0.85f); // cyan
                case PortType.Water:    return new UnityEngine.Color(0.35f, 0.65f, 0.90f); // deep sky
                case PortType.Image:    return new UnityEngine.Color(0.90f, 0.65f, 0.75f); // UC-T07-02: separate pixel/texture-reference wires.
                case PortType.Vector:   return new UnityEngine.Color(0.55f, 0.85f, 0.70f); // UC-T07-03: preserve all earlier serialized port values.
                case PortType.Climate:  return new UnityEngine.Color(0.35f, 0.80f, 0.60f); // UC-T08-03: append, preserving existing saved wires.
                case PortType.Weather:  return new UnityEngine.Color(0.45f, 0.70f, 0.90f); // UC-T08-06: typed region recipes have their own visible connection colour.
                default:                return new UnityEngine.Color(0.70f, 0.70f, 0.70f);
            }
        }
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

        // A third slot for the scatter nodes, and a separate array rather than a
        // third entry in HeightMaskIn because the filter nodes share that one and
        // would grow a Biome socket they cannot read. The Biome port is optional: a
        // graph that leaves it unwired scatters exactly as it did before, since an
        // absent classification withdraws every species' biome preference.
        //
        // Wired as a port instead of taken from the tile, because a port is what
        // makes the classifier a dependency of the scatter node. The evaluator walks
        // inputs to decide what to run first, so the biome field is guaranteed built
        // before anything reads it - where a shared side-channel would work or not
        // depending on the order nodes happen to sit in the list.
        public static readonly Port[] HeightMaskBiomeIn =
        {
            new Port("Height", PortType.Matrix, PortDirection.Input),
            new Port("Mask",   PortType.Matrix, PortDirection.Input),
            new Port("Biome",  PortType.Matrix, PortDirection.Input)
        };

        public static readonly Port[] TwoHeightIn =
        {
            new Port("A", PortType.Matrix, PortDirection.Input),
            new Port("B", PortType.Matrix, PortDirection.Input)
        };

        // A single INPUT port that accepts MANY wires (fan-in), folded into one
        // field by a MergeOp the consuming node chooses. allowMultiple = true is
        // what tells Graph.AddConnection to accumulate edges here instead of
        // evicting the previous one, and what the fan-in evaluator path keys on.
        public static readonly Port[] ManyHeightIn =
        {
            new Port("Inputs", PortType.Matrix, PortDirection.Input, true)
        };

        public static readonly Port[] TwoHeightMaskIn =
        {
            new Port("A",    PortType.Matrix, PortDirection.Input),
            new Port("B",    PortType.Matrix, PortDirection.Input),
            new Port("Mask", PortType.Matrix, PortDirection.Input)
        };
    }

    /// <summary>
    /// Port arrays for the texture graph. Separate class from SharedPorts so the
    /// two graphs' vocabularies stay visibly distinct in code - a node reaching
    /// into the wrong one is then obvious at the call site rather than at the
    /// moment a wire refuses to connect.
    ///
    /// Same no-allocation rule applies: these are read on every editor repaint.
    /// </summary>
    public static class TexturePorts
    {
        // -- outputs ---------------------------------------------------------
        public static readonly Port[] LayerOut =
        {
            new Port("Layer", PortType.Layer, PortDirection.Output, true)
        };

        public static readonly Port[] SplatOut =
        {
            new Port("Splat", PortType.Splat, PortDirection.Output, true)
        };

        public static readonly Port[] ColorOut =
        {
            new Port("Colour", PortType.Color, PortDirection.Output, true)
        };

        public static readonly Port[] ScalarOut =
        {
            new Port("Value", PortType.Scalar, PortDirection.Output, true)
        };

        public static readonly Port[] GradientOut =
        {
            new Port("Gradient", PortType.Gradient, PortDirection.Output, true)
        };

        public static readonly Port[] SurfaceOut =
        {
            new Port("Surface", PortType.Surface, PortDirection.Output, true)
        };

        public static readonly Port[] TextureOut =
        {
            new Port("Texture", PortType.Texture, PortDirection.Output, true)
        };

        // UC-T07-02: conversion between CPU pixels and native references is explicit.
        public static readonly Port[] ImageOut = { new Port("Image", PortType.Image, PortDirection.Output, true) };
        public static readonly Port[] ImageIn = { new Port("Image", PortType.Image, PortDirection.Input) };
        /// <summary>UC-T07-03: a constant four-component parameter, without colour decoding.</summary>
        public static readonly Port[] VectorOut = { new Port("Vector", PortType.Vector, PortDirection.Output, true) };

        public static readonly Port[] UVOut =
        {
            new Port("UV", PortType.UV, PortDirection.Output, true)
        };

        public static readonly Port[] WaterOut =
        {
            new Port("Water", PortType.Water, PortDirection.Output, true)
        };

        // -- inputs ----------------------------------------------------------
        public static readonly Port[] LayerIn =
        {
            new Port("Layer", PortType.Layer, PortDirection.Input)
        };

        public static readonly Port[] SurfaceIn =
        {
            new Port("Surface", PortType.Surface, PortDirection.Input)
        };

        /// <summary>
        /// The standard shape for a surface feature: take the surface built so
        /// far, add one thing to it, hand it on. Feature order in the graph then
        /// mirrors the order the shader applies them, which is the only ordering
        /// a user can reason about.
        /// </summary>
        public static readonly Port[] SurfaceChainIn =
        {
            new Port("Surface", PortType.Surface, PortDirection.Input)
        };

        public static readonly Port[] FourLayerIn =
        {
            new Port("Layer 0", PortType.Layer, PortDirection.Input),
            new Port("Layer 1", PortType.Layer, PortDirection.Input),
            new Port("Layer 2", PortType.Layer, PortDirection.Input),
            new Port("Layer 3", PortType.Layer, PortDirection.Input)
        };

        public static readonly Port[] TwoColorIn =
        {
            new Port("A", PortType.Color, PortDirection.Input),
            new Port("B", PortType.Color, PortDirection.Input)
        };
    }
}
