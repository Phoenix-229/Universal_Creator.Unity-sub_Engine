// UC-T07-07: typed value expressions reach actual supported shader parameters without becoming image pixels or arbitrary shader programs.
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace UniverseCreator.Nodes.Texturing
{
    /// <summary>Supported material uniform value type, distinct from images and texture references.</summary>
    public enum ShaderParameterKind { Scalar = 0, Colour = 1, Vector = 2 }

    /// <summary>Writes one typed parameter to a copied surface after validating the actual destination shader property.</summary>
    /// <remarks>Scalar/colour/vector expressions are evaluated values consumed by existing fragment shader uniforms.
    /// This node does not generate vertex/compute programs or add variants. Use the explicit Run/Apply owner for renderer changes.</remarks>
    [Serializable]
    public sealed class ShaderParameterNode : TextureNode
    {
        private static readonly Port[] Inputs =
        {
            new Port("Surface", PortType.Surface, PortDirection.Input), new Port("Scalar", PortType.Scalar, PortDirection.Input),
            new Port("Colour", PortType.Color, PortDirection.Input), new Port("Vector", PortType.Vector, PortDirection.Input)
        };
        /// <summary>Actual optional target shader; null selects installed UniverseCreator/TerrainLit.</summary>
        [Tooltip("Optional actual shader asset. Empty selects UniverseCreator/TerrainLit; unsupported properties are diagnosed.")]
        public Shader shader;
        /// <summary>Exact exposed property name on the selected shader, for example _Brightness.</summary>
        [Tooltip("Exact exposed shader property name. Its native property type must match the chosen parameter kind.")]
        public string propertyName = UCShaderProps.Brightness;
        /// <summary>Scalar, Colour or Vector value type; unused ports remain visible to preserve serialized wire indices.</summary>
        public ShaderParameterKind kind;
        /// <summary>Finite scalar fallback; units/range belong to the selected shader property.</summary>
        public float scalar = 1;
        /// <summary>Finite linear RGBA fallback, used only for a native Color property.</summary>
        [ColorUsage(true, true)] public Color colour = Color.white;
        /// <summary>Finite four-component fallback, used only for a native Vector property.</summary>
        public Vector4 vector;
        public override string NodeName => "Shader Parameter";
        public override string Tooltip => "Validated scalar/colour/vector expressions for the actual supported shader; no terrain or texture bake.";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.MainThread;
        public override string SettingsDiagnostic => string.IsNullOrEmpty(propertyName) ? "Choose an exposed shader property name." :
            (!Enum.IsDefined(typeof(ShaderParameterKind), kind) ? "Select Scalar, Colour or Vector parameter type." : null);
        public override Port[] GetInputs() => Inputs;
        public override Port[] GetOutputs() => TexturePorts.SurfaceOut;

        /// <summary>Validates the selected exposed property and publishes its resolved typed value on an independent surface.</summary>
        /// <param name="outputIndex">Sole surface output, zero.</param>
        /// <param name="tile">Bound graph input/lifetime owner.</param>
        /// <param name="stop">Optional cancellation; cancelled values never publish.</param>
        /// <returns>Copied surface description, or null when cancelled.</returns>
        /// <exception cref="ArgumentException">Kind, property name/type or value is incompatible.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            GraphExecutionGuard.RequireMainThread("Shader parameter authoring");
            if (stop != null && stop.StopRequested) return null;
            if (!Enum.IsDefined(typeof(ShaderParameterKind), kind)) throw new ArgumentException("Unsupported shader parameter kind.");
            var schema = ShaderOutputDescriptor.PropertySchema(shader != null ? shader : Shader.Find(UCShaderProps.ShaderName));
            ShaderPropertyType expected = kind == ShaderParameterKind.Scalar ? ShaderPropertyType.Float :
                kind == ShaderParameterKind.Colour ? ShaderPropertyType.Color : ShaderPropertyType.Vector;
            if (string.IsNullOrEmpty(propertyName) || !schema.TryGetValue(propertyName, out ShaderPropertyType actual) ||
                (actual != expected && !(expected == ShaderPropertyType.Float && actual == ShaderPropertyType.Range)))
                throw new ArgumentException("Shader parameter '" + propertyName + "' is absent or has an incompatible type; choose an exposed " + expected + " property.");
            var surface = GetInputSurface(tile, 0, stop)?.Copy() ?? new SurfaceProduct();
            if (kind == ShaderParameterKind.Scalar)
            { float value = GetInputScalarOr(tile, 1, stop, scalar); TextureImageSampling.RequireFinite(value); surface.SetFloat(propertyName, value); }
            else if (kind == ShaderParameterKind.Colour)
            { Color value = TryGetInputColor(tile, 2, stop, out Color input) ? input : colour; TextureImageSampling.RequireFinite(value); surface.SetColor(propertyName, value); }
            else
            { object input = tile.PullInput(this, 3, stop); surface.SetVector(propertyName, input is Vector4 value ? value : vector); }
            return stop != null && stop.StopRequested ? null : surface;
        }
    }

    /// <summary>Finite scalar value expression using the existing Colour Math equations, independent of image and shader resource work.</summary>
    [Serializable]
    public sealed class TextureScalarMathNode : TextureNode
    {
        private static readonly Port[] Inputs =
        {
            new Port("A", PortType.Scalar, PortDirection.Input), new Port("B", PortType.Scalar, PortDirection.Input)
        };
        /// <summary>Existing shared scalar equation; interpolation/screen/overlay keep their documented colour-math conventions.</summary>
        public ColorMathOp operation = ColorMathOp.Multiply;
        /// <summary>Finite fallback for unconnected A.</summary>
        public float a = 1;
        /// <summary>Finite fallback for unconnected B.</summary>
        public float b = 1;
        /// <summary>Finite interpolation factor 0…1 for Lerp.</summary>
        [Range(0, 1)] public float factor = 0.5f;
        public override string NodeName => "Scalar Math";
        public override string Tooltip => "Reusable scalar expression for supported material parameters; no image allocation.";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override Port[] GetInputs() => Inputs;
        public override Port[] GetOutputs() => TexturePorts.ScalarOut;

        /// <summary>Evaluates the selected shared scalar equation.</summary>
        /// <param name="outputIndex">Sole scalar output, zero.</param>
        /// <param name="tile">Bound input/cache owner.</param>
        /// <param name="stop">Optional cooperative cancellation.</param>
        /// <returns>Finite float value, or null when cancelled.</returns>
        /// <exception cref="ArgumentException">Equation, values, factor or result is invalid.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (stop != null && stop.StopRequested) return null;
            float x = GetInputScalarOr(tile, 0, stop, a), y = GetInputScalarOr(tile, 1, stop, b);
            TextureImageSampling.RequireFinite(x); TextureImageSampling.RequireFinite(y); TextureImageSampling.RequireFinite(factor);
            if (!Enum.IsDefined(typeof(ColorMathOp), operation) || factor < 0 || factor > 1) throw new ArgumentException("Scalar math needs a supported equation and interpolation factor 0…1.");
            float value = ColorMathNode.Calculate(operation, x, y, factor); TextureImageSampling.RequireFinite(value);
            return stop != null && stop.StopRequested ? null : (object)value;
        }
    }
}
