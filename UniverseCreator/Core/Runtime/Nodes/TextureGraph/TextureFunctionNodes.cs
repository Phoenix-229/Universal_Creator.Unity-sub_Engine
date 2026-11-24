// UC-T07-08: reusable typed functions use ordinary Graph assets and the shared snapshot/evaluator owners.
using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Nodes.Texturing
{
    /// <summary>Named typed function parameter; constants default locally and resource inputs require a caller wire.</summary>
    [Serializable]
    public sealed class TextureFunctionInputNode : TextureNode
    {
        /// <summary>Unique input name within this function; changing the name preserves the endpoint ID and caller slot.</summary>
        public string parameterName = "Input";
        /// <summary>Serialized wire currency; images, texture handles, material descriptions and expressions remain distinct.</summary>
        public PortType type = PortType.Scalar;
        /// <summary>Finite scalar default for an unconnected Scalar parameter; its physical units belong to the function.</summary>
        public float scalar;
        /// <summary>Finite linear colour default for an unconnected Color parameter.</summary>
        [ColorUsage(true, true)] public Color colour = Color.white;
        /// <summary>Finite vector default for an unconnected Vector parameter.</summary>
        public Vector4 vector;
        [NonSerialized] private Port[] ports;
        public override string NodeName => "Function Input";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override string SettingsDiagnostic => string.IsNullOrWhiteSpace(parameterName) || !Enum.IsDefined(typeof(PortType), type)
            ? "Name this function input and select a supported type." : null;
        public override Port[] GetInputs() => SharedPorts.None;
        public override Port[] GetOutputs()
        {
            if (ports == null || ports[0].name != parameterName || ports[0].type != type)
                ports = new[] { new Port(parameterName, type, PortDirection.Output) };
            return ports;
        }
        /// <summary>Returns the caller's borrowed input or the declared constant default.</summary>
        /// <param name="outputIndex">Sole output, zero.</param>
        /// <param name="tile">Function view sharing the caller's world, cancellation and product lifetime.</param>
        /// <param name="stop">Optional cooperative cancellation.</param>
        /// <returns>Typed read-only product or null when cancelled.</returns>
        /// <exception cref="InvalidOperationException">A resource parameter is unwired; connect it in the caller or preview through a caller.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (stop != null && stop.StopRequested) return null;
            object input = tile.PullFunctionArgument(id, stop);
            if (input != null) return input;
            if (type == PortType.Scalar) { TextureImageSampling.RequireFinite(scalar); return scalar; }
            if (type == PortType.Color) { TextureImageSampling.RequireFinite(colour); return colour; }
            if (type == PortType.Vector)
            {
                for (int i = 0; i < 4; i++) TextureImageSampling.RequireFinite(vector[i]);
                return vector;
            }
            throw new InvalidOperationException("Function input '" + parameterName + "' needs a connected " + type + " product. Preview it through a Function Call.");
        }
    }

    /// <summary>Named typed function result; this endpoint returns a product without applying world or material outputs.</summary>
    [Serializable]
    public sealed class TextureFunctionOutputNode : TextureNode
    {
        /// <summary>Unique output name within the function; stable node ID preserves the caller's serialized slot.</summary>
        public string resultName = "Result";
        /// <summary>Declared input/output currency; incompatible wires receive normal graph diagnostics.</summary>
        public PortType type = PortType.Scalar;
        [NonSerialized] private Port[] inputs, outputs;
        public override string NodeName => "Function Output";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override string SettingsDiagnostic => string.IsNullOrWhiteSpace(resultName) || !Enum.IsDefined(typeof(PortType), type)
            ? "Name this function output and select a supported type." : null;
        public override Port[] GetInputs()
        {
            if (inputs == null || inputs[0].name != resultName || inputs[0].type != type)
                inputs = new[] { new Port(resultName, type, PortDirection.Input) };
            return inputs;
        }
        public override Port[] GetOutputs()
        {
            if (outputs == null || outputs[0].name != resultName || outputs[0].type != type)
                outputs = new[] { new Port(resultName, type, PortDirection.Output) };
            return outputs;
        }
        /// <summary>Returns the wired read-only result; an absent resource is diagnosed at the endpoint.</summary>
        /// <param name="outputIndex">Sole result, zero.</param>
        /// <param name="tile">Bound function tile.</param>
        /// <param name="stop">Optional cooperative cancellation.</param>
        /// <returns>Borrowed input or null when cancelled.</returns>
        /// <exception cref="InvalidOperationException">The named output is unwired.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (stop != null && stop.StopRequested) return null;
            object result = tile.PullInput(this, 0, stop);
            if (stop != null && stop.StopRequested) return null;
            return result ?? throw new InvalidOperationException("Connect function output '" + resultName + "' to a " + type + " product.");
        }
    }

    /// <summary>One saved caller port bound by endpoint ID; slots remain stable across renames, copy/paste and reload.</summary>
    [Serializable]
    public sealed class TextureFunctionPort
    {
        /// <summary>Function endpoint node ID, independent of its display name.</summary>
        public string endpointId;
        /// <summary>Last explicitly refreshed name displayed on the call socket.</summary>
        public string name;
        /// <summary>Original socket type; a retyped endpoint is diagnosed rather than silently rewiring saved graphs.</summary>
        public PortType type;
    }

    /// <summary>Calls a reusable Texture-domain Graph with named typed parameters and captured dependency hashes.</summary>
    /// <remarks>Refresh ports explicitly after adding endpoints. Removed/retyped slots stay present and diagnosable;
    /// restore the endpoint or recreate the affected call after reconnecting its wires. Each parent tile evaluates
    /// one child view shared by all outputs. Nested graphs are limited to eight levels and 1,024 nodes per call tree.</remarks>
    [Serializable]
    public sealed class TextureFunctionCallNode : TextureNode, IGenerationSnapshotDependencies, IPreparedGraphInputHash
    {
        /// <summary>Reusable Texture-domain Graph asset containing Function Input/Output nodes and no Apply terminals.</summary>
        [Tooltip("Texture Graph with named Function Input/Output nodes. Refresh Function Ports after adding endpoints; recursive calls are rejected.")]
        public Graph function;
        /// <summary>Saved input slots; mutated only by explicit port refresh, preserving serialized wire indices.</summary>
        [HideInInspector] public List<TextureFunctionPort> inputPorts = new List<TextureFunctionPort>();
        /// <summary>Saved output slots; removed/retyped endpoint IDs are recoverable diagnostics.</summary>
        [HideInInspector] public List<TextureFunctionPort> outputPorts = new List<TextureFunctionPort>();
        [NonSerialized] private Port[] inputs, outputs;
        [NonSerialized] private GenerationSnapshot captured;
        [NonSerialized] private ulong dependencyHash;
        internal Graph CapturedGraph => captured?.Graph;
        internal static bool UsesAcceptedWorldInputs(Graph graph)
        {
            foreach (Generator node in graph.nodes)
            {
                if (node.disabled) continue;
                if (node is WorldGraphInputNode) return true;
                if (node is TextureFunctionCallNode call && call.CapturedGraph != null && UsesAcceptedWorldInputs(call.CapturedGraph)) return true;
            }
            return false;
        }
        public override string NodeName => "Function Call";
        public override NodeExecutionDomain ExecutionDomain => NodeExecutionDomain.Worker;
        public override string SettingsDiagnostic
        {
            get
            {
                int count = 0;
                return Diagnose(function, new HashSet<Graph>(), 0, ref count) ?? DiagnoseSlots(function, inputPorts, true) ??
                    DiagnoseSlots(function, outputPorts, false) ?? (outputPorts == null || outputPorts.Count == 0 ? "Refresh Function Ports to expose named results." : null);
            }
        }
        public override Port[] GetInputs() => inputs ?? (inputs = BuildPorts(inputPorts, PortDirection.Input));
        public override Port[] GetOutputs() => outputs ?? (outputs = BuildPorts(outputPorts, PortDirection.Output));

        /// <summary>Appends new endpoint slots and updates names; never removes slots or silently changes their type.</summary>
        /// <exception cref="InvalidOperationException">The function is absent, recursive or exceeds its bounded graph envelope.</exception>
        /// <remarks>Main-thread editor operation; the caller records graph Undo before invoking and rebuilds its canvas afterward.</remarks>
        public void RefreshPorts()
        {
            int count = 0; string error = Diagnose(function, new HashSet<Graph>(), 0, ref count);
            if (error != null) throw new InvalidOperationException(error);
            if (inputPorts == null) inputPorts = new List<TextureFunctionPort>();
            if (outputPorts == null) outputPorts = new List<TextureFunctionPort>();
            foreach (Generator node in function.nodes)
            {
                if (node is TextureFunctionInputNode input) Refresh(inputPorts, input.id, input.parameterName, input.type);
                if (node is TextureFunctionOutputNode output) Refresh(outputPorts, output.id, output.resultName, output.type);
            }
            inputs = outputs = null; MarkDirty();
        }

        private static void Refresh(List<TextureFunctionPort> ports, string id, string name, PortType type)
        {
            foreach (var port in ports) if (port.endpointId == id) { port.name = name; return; }
            ports.Add(new TextureFunctionPort { endpointId = id, name = name, type = type });
        }
        private static Port[] BuildPorts(List<TextureFunctionPort> slots, PortDirection direction)
        {
            if (slots == null) return SharedPorts.None;
            var result = new Port[slots.Count];
            for (int i = 0; i < result.Length; i++) result[i] = new Port(slots[i]?.name ?? "Missing endpoint", slots[i]?.type ?? PortType.Scalar, direction);
            return result;
        }
        private static string DiagnoseSlots(Graph graph, List<TextureFunctionPort> slots, bool input)
        {
            if (slots == null) return "Refresh Function Ports to recover the saved signature.";
            foreach (var port in slots)
            {
                Generator endpoint = port == null ? null : graph.GetNodeById(port.endpointId);
                PortType? type = input && endpoint is TextureFunctionInputNode a ? a.type :
                    !input && endpoint is TextureFunctionOutputNode b ? b.type : (PortType?)null;
                if (port == null || !type.HasValue || type.Value != port.type) return "Function port '" + port?.name + "' was removed or retyped. Restore its endpoint or recreate this call and reconnect its ports.";
            }
            return null;
        }
        private static string Diagnose(Graph graph, HashSet<Graph> visiting, int depth, ref int count)
        {
            if (ReferenceEquals(graph, null)) return "Select a Texture Graph function and refresh its named ports.";
            if (depth >= 8 || !visiting.Add(graph)) return "Function recursion/depth is unsupported; remove the recursive call or reduce nesting below eight levels.";
            try
            {
                if (!graph.HasSupportedDomainMetadata || graph.Domain != UCGraphDomain.TextureMaterialShader || graph.nodes == null) // UC-T07-07: use the existing serialized graph domain.
                    return "Function assets must be Texture-domain Graphs with named Function Input/Output nodes.";
                count += graph.nodes.Count;
                if (count > 1024) return "Function dependency tree exceeds the 1,024-node limit; split the function.";
                var names = new HashSet<string>(); var resultNames = new HashSet<string>();
                foreach (Generator node in graph.nodes)
                {
                    if (node == null || node is IOutputNode) return "Restore missing function nodes and replace Apply terminals with Function Output endpoints.";
                    if (node is TextureFunctionInputNode input && (input.SettingsDiagnostic != null || !names.Add(input.parameterName))) return "Function input names must be nonempty, unique and typed.";
                    if (node is TextureFunctionOutputNode output && (output.SettingsDiagnostic != null || !resultNames.Add(output.resultName))) return "Function output names must be nonempty, unique and typed.";
                    if (node is TextureFunctionCallNode call)
                    {
                        string error = Diagnose(call.function, visiting, depth + 1, ref count);
                        if (error != null) return error;
                    }
                }
                return resultNames.Count == 0 ? "Add at least one named Function Output endpoint." : null;
            }
            finally { visiting.Remove(graph); }
        }

        /// <summary>Captures the child graph before worker dispatch and fingerprints all captured node dependencies.</summary>
        /// <param name="world">Parent's bound world or null for standalone inputs.</param>
        /// <param name="domains">Execution stages supported by the parent executor.</param>
        /// <exception cref="InvalidOperationException">Signature, recursion, topology or execution stages are unsupported.</exception>
        public void CaptureDependencies(UniverseCreatorObject world, NodeExecutionDomain domains)
        {
            ReleaseDependencies();
            string error = SettingsDiagnostic;
            if (error != null) throw new InvalidOperationException(error);
            captured = GenerationSnapshot.CaptureForDomains(function, domains, world);
            ulong hash = 14695981039346656037UL;
            foreach (Generator node in captured.Graph.nodes)
            {
                // UC-T07-07: Mix consumes 32 bits; include both halves so upper-bit dependency changes invalidate the function.
                hash = PreparedRasterInputs.Mix(hash, (uint)node.contentHash);
                hash = PreparedRasterInputs.Mix(hash, (uint)(node.contentHash >> 32));
            }
            dependencyHash = hash == 0 ? 1UL : hash;
        }
        /// <summary>Releases this detached call's dependency owner; live child tile leases keep it valid until retirement.</summary>
        public void ReleaseDependencies() { captured?.Release(); captured = null; dependencyHash = 0; }
        /// <summary>Returns the captured body/dependency fingerprint; zero before capture forces conservative cache invalidation.</summary>
        public ulong GetPreparedInputHash() => dependencyHash;

        /// <summary>Pulls one stable named result through the existing per-tile evaluator/cache.</summary>
        /// <param name="outputIndex">Saved output slot, zero through output count minus one.</param>
        /// <param name="tile">Parent tile retaining accepted world inputs and function child views.</param>
        /// <param name="stop">Optional cooperative cancellation propagated to all child/argument pulls.</param>
        /// <returns>Borrowed typed output or null when cancelled.</returns>
        /// <exception cref="InvalidOperationException">Call was not captured or a saved endpoint is absent.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (stop != null && stop.StopRequested) return null;
            if (captured == null) throw new InvalidOperationException("Capture the graph before evaluating a Function Call; its dependency asset must remain immutable during the run.");
            if (outputIndex < 0 || outputIndex >= outputPorts.Count) throw new ArgumentOutOfRangeException(nameof(outputIndex));
            TileData child = tile.FunctionView(this, captured);
            return child.PullFunctionResult(captured.Graph.GetNodeById(outputPorts[outputIndex].endpointId), stop);
        }
        internal object PullArgument(string endpointId, TileData parent, StopToken stop)
        {
            for (int i = 0; i < inputPorts.Count; i++) if (inputPorts[i].endpointId == endpointId) return parent.PullInput(this, i, stop);
            return null;
        }
    }
}
