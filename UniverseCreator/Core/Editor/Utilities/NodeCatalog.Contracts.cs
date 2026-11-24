// UC-T03-03: executable Node Graph contract inventory, built from the same catalog
// as the add-node menu. Reflection/default probing is editor-only and on demand.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Editor.Utilities
{
    public static partial class NodeCatalog
    {
        [Serializable]
        internal sealed class GroundContract
        {
            public string type, name, role, execution, disabledBehavior, defaults;
            public string preparation, publication;
            public Port[] inputs, outputs;
            // UC-T03-03: units are aligned with the real port arrays, not inferred from Matrix compatibility.
            public string[] inputUnits, outputUnits;
            public ControlContract[] controls;
            public string[] compatibleConsumers;
        }

        [Serializable]
        internal sealed class ControlContract
        {
            public string field, type, defaultValue, description, units, activeWhen;
            // Bounds below are declared inspector metadata, not proof of runtime clamping.
            public bool hasMinimum, hasMaximum, hiddenInInspector;
            public float minimum, maximum;
        }

        [Serializable]
        internal sealed class ConnectionContract
        {
            public string sourceId, sourceType, sourcePort, targetId, targetType, targetPort;
            public int sourceIndex, targetIndex;
            public PortType valueType;
            public string sourceUnits, targetUnits;
            public bool sourceDisabled, targetDisabled, terminalConsumer;
        }

        /// <summary>UC-T03-03: records the authored wires of a validated Node Graph, including disabled and terminal consumers.</summary>
        /// <param name="graph">Prepared graph with current port types and compiled topology; null returns an empty inventory.</param>
        /// <returns>Connections in authored order. These records prove wiring only, not successful execution or a final terrain effect.</returns>
        /// <exception cref="InvalidOperationException">The graph has invalid topology/domains or has not been compiled.</exception>
        /// <remarks>Main thread, read-only. Wireless dependencies are available through the existing compiled plan; no second resolver is introduced.</remarks>
        internal static ConnectionContract[] CaptureGroundConnections(Graph graph)
        {
            if (graph == null) return Array.Empty<ConnectionContract>();
            GraphValidationReport.Analyze(graph, NodeExecutionDomain.Worker, false).ThrowIfInvalid();
            if (graph.compiledPlan == null) throw new InvalidOperationException("Capture connections from a compiled graph snapshot.");
            var result = new List<ConnectionContract>(graph.connections.Count);
            foreach (Connection edge in graph.connections)
            {
                Generator source = graph.GetNodeById(edge.sourceNodeId), target = graph.GetNodeById(edge.targetNodeId);
                Port sourcePort = source.GetOutputs()[edge.sourcePortIndex], targetPort = target.GetInputs()[edge.targetPortIndex];
                result.Add(new ConnectionContract {
                    sourceId = source.id, sourceType = source.GetType().FullName,
                    sourceIndex = edge.sourcePortIndex, sourcePort = source.GetOutputs()[edge.sourcePortIndex].name,
                    targetId = target.id, targetType = target.GetType().FullName,
                    targetIndex = edge.targetPortIndex, targetPort = target.GetInputs()[edge.targetPortIndex].name,
                    valueType = sourcePort.type, sourceUnits = ReviewedPortUnits(source, sourcePort),
                    targetUnits = ReviewedPortUnits(target, targetPort),
                    sourceDisabled = source.disabled, targetDisabled = target.disabled, terminalConsumer = target is IOutputNode
                });
            }
            return result.ToArray();
        }

        /// <summary>Captures shipped Node Graph ports, authored defaults and declared control bounds for the audit.</summary>
        /// <returns>Deterministically ordered records; missing unit/range declarations remain explicit gaps.</returns>
        /// <remarks>Main thread only. Never evaluates generators or reads user graph/scene values.
        /// Compatible consumers are type-compatible catalog entries, not proof of a connected production graph.</remarks>
        internal static GroundContract[] CaptureGroundContracts()
        {
            var contracts = new List<GroundContract>();
            var consumerPorts = new List<KeyValuePair<string, Port[]>>();
            foreach (NodeCatalogEntry entry in All)
            {
                if (entry.type.Assembly != typeof(Generator).Assembly) continue;
                Generator node = Instantiate(entry);
                Port[] inputs = node.GetInputs(), outputs = node.GetOutputs();
                // UC-T03-03: ground fields also feed biome/scatter/material consumers outside this producer inventory.
                consumerPorts.Add(new KeyValuePair<string, Port[]>(entry.type.FullName, inputs));
                if (NodeCategoryColors.IsTextureGraph(entry.category) || entry.category == NodeCategory.Scatter ||
                    entry.category == NodeCategory.Biome) continue;
                var controls = new List<ControlContract>();
                for (Type type = entry.type; type != null && type != typeof(Generator); type = type.BaseType)
                {
                    foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (field.IsStatic || field.IsInitOnly || field.IsNotSerialized ||
                            (!field.IsPublic && !field.IsDefined(typeof(SerializeField), true))) continue;
                        var range = field.GetCustomAttribute<RangeAttribute>();
                        var min = field.GetCustomAttribute<MinAttribute>();
                        var tooltip = field.GetCustomAttribute<TooltipAttribute>();
                        object value = field.GetValue(node);
                        controls.Add(new ControlContract {
                            field = type.FullName + "." + field.Name, type = field.FieldType.FullName,
                            defaultValue = DefaultDescription(value),
                            description = tooltip != null ? tooltip.tooltip : "No runtime unit/description metadata; review source documentation.",
                            units = ReviewedControlUnits(field), activeWhen = ControlCondition(field),
                            hiddenInInspector = field.IsDefined(typeof(HideInInspector), true),
                            hasMinimum = range != null || min != null, hasMaximum = range != null,
                            minimum = range != null ? range.min : min != null ? min.min : 0f,
                            maximum = range != null ? range.max : 0f
                        });
                    }
                }
                controls.Sort((a, b) => string.CompareOrdinal(a.field, b.field));
                contracts.Add(new GroundContract {
                    type = entry.type.FullName, name = entry.nodeName,
                    role = node is IOutputNode ? "Output" : node is CreatorNode ? "Creator" : node is SelectorNode ? "Mask" : entry.category.ToString(),
                    execution = node.ExecutionDomain.ToString(), defaults = JsonUtility.ToJson(node),
                    preparation = PreparationContract(node), publication = PublicationContract(node),
                    disabledBehavior = outputs.Length == 0 ? "No output write."
                        : "First non-null input matching the requested outlet type passes through; otherwise null.",
                    inputs = (Port[])inputs.Clone(), outputs = (Port[])outputs.Clone(), controls = controls.ToArray(),
                    inputUnits = CapturePortUnits(node, inputs), outputUnits = CapturePortUnits(node, outputs)
                });
            }
            contracts.Sort((a, b) => string.CompareOrdinal(a.type, b.type));
            foreach (GroundContract producer in contracts)
            {
                var consumers = new List<string>();
                foreach (Port output in producer.outputs)
                    foreach (KeyValuePair<string, Port[]> consumer in consumerPorts)
                        foreach (Port input in consumer.Value)
                            if (input.type == output.type)
                                consumers.Add(output.name + " -> " + consumer.Key + "." + input.name);
                consumers.Sort(StringComparer.Ordinal);
                producer.compatibleConsumers = consumers.ToArray();
            }
            return contracts.ToArray();
        }

        // UC-T03-03: a Matrix carries no unit tag. Record each reviewed producer/consumer contract
        // without changing wire compatibility or implying that a wire performs unit conversion.
        private static string[] CapturePortUnits(Generator node, Port[] ports)
        {
            var result = new string[ports.Length];
            for (int i = 0; i < ports.Length; i++) result[i] = ReviewedPortUnits(node, ports[i]);
            return result;
        }

        // UC-T03-03: report implemented hooks without invoking preparation or claiming captured scene data exists.
        private static string PreparationContract(Generator node)
        {
            Type owner = node.GetType().GetMethod(nameof(Generator.PrepareMainThread)).DeclaringType;
            string result = owner == typeof(Generator) ? "No PrepareMainThread override." : "Main-thread preparation: " + owner.FullName + ".PrepareMainThread.";
            if (node is IGenerationSnapshotInputs) result += " Copies external inputs through IGenerationSnapshotInputs.";
            if (node is IPreparedGraphInputHash) result += " Prepared values use GetPreparedInputHash.";
            else if (node is IPreparedGraphInputs) result += " Prepared values use GetPreparedInputsForHash.";
            return result;
        }

        private static string PublicationContract(Generator node)
        {
            switch (node.GetType().Name)
            {
                case "HeightOutputNode": return "OutputBag.heightmap: normalized heights consumed by TerrainBuilder; native terrain conversion uses the world settings.";
                case "TextureOutputNode": case "BiomeSplatOutputNode": return "OutputBag.splatmap: normalized layer weights consumed by TerrainBuilder; Biome Splat also supplies layer texture descriptors.";
                case "GrassOutputNode": return "OutputBag.detailLayers: integer cell densities keyed by detailLayerIndex, consumed by terrain detail commit.";
                case "HolesOutputNode": return "OutputBag.holes and requestColliderRebuild: native terrain hole/collider commit.";
                case "NormalMapOutputNode": return "OutputBag.normals: unit vectors for custom consumers; native TerrainData derives normals from heights.";
                case "CustomMeshOutputNode": return "OutputBag.meshes[meshChannel]: mesh buffers for a separate scene consumer, not a TerrainData height write.";
                case "WindOutputNode": return "OutputBag wind sway amplitude/phase: published with accepted terrain for the wind consumer.";
                case "WeatherOutputNode": return "OutputBag weather bias fields: published with accepted terrain for the weather consumer.";
                case "LightingOutputNode": return "OutputBag lighting bias fields: published with accepted terrain for the lighting consumer.";
                default: return "Pull product; consumers are recorded from authored wires. A field alone does not create scene objects.";
            }
        }

        private static string ReviewedPortUnits(Generator node, Port port)
        {
            string type = node.GetType().Name, name = port.name;
            bool output = port.direction == PortDirection.Output;
            if (node is IPortalIn || node is IPortalOut || type == "PreviewNode")
                return "Inherited from the routed product; no unit conversion.";
            if (type == "RandomSeedNode") return "Deterministic integer seed stored as a float (precision may be lost).";
            if (type == "WorldGraphInputNode") // UC-T05-06: every accepted output retains its physical interpretation.
            {
                if (name.Contains("Coverage") || name.Contains("Membership")) return "Dimensionless accepted coverage/membership, 0..1; categories are decoded before sampling.";
                if (name.StartsWith("Ground Elevation", StringComparison.Ordinal)) return "Accepted elevation in world metres; no normalization."; // UC-T07-11.
                if (name.StartsWith("Ground Slope", StringComparison.Ordinal)) return "Accepted slope in degrees, acos of the world normal's Y component.";
                if (name.StartsWith("Water Bed Depth", StringComparison.Ordinal)) return "Accepted nonnegative water-surface minus bed elevation in metres; dry positions are zero.";
                if (name.StartsWith("Water Current", StringComparison.Ordinal)) return "Accepted water-current component in world XZ metres/second; dry positions are zero.";
                if (name.StartsWith("Selected Shore Distance", StringComparison.Ordinal)) return "Horizontal signed world metres, negative wet/positive dry, clamped to shoreSearchMetres (at most 256 metres).";
                if (name.StartsWith("Surface Normal", StringComparison.Ordinal)) return "Dimensionless unit-normal component in world axes, -1..1 (Y 0..1).";
                if (name.Contains("Temperature")) return "Accepted temperature bias in degrees Celsius; no implicit normalization.";
                if (name.Contains("Precipitation")) return "Accepted precipitation bias in millimetres per hour; no implicit normalization.";
                if (name.Contains("Wind")) return "Accepted wind-speed bias in metres per second; no implicit normalization.";
                if (port.type == PortType.Surface) return "Immutable accepted surface descriptor; channel units follow its material consumers.";
                return "Final accepted world elevation normalized to the tile's vertical datum and height in metres.";
            }
            // UC-T08-03/06: appended Lighting Graph recipe inputs keep their physical units through accepted publication.
            if (port.type == PortType.Climate && type == "WeatherOutputNode")
                return "Immutable climate recipe: temperature Celsius, moisture 0..1, wind vector metres/second; seasonal model-year fraction and region XZ bounds/blend in metres.";
            if (port.type == PortType.Weather && type == "WeatherOutputNode")
                return "Immutable weather regions: XZ bounds/blend metres, drift and wind metres/second, temperature Celsius, rain millimetres/hour, visibility metres; humidity/cloud/fog/wetness 0..1.";
            if (port.type != PortType.Matrix) return "Unreviewed — inspect this typed product's consumer.";
            if (type == "WeatherOutputNode" || type == "LightingOutputNode")
                return "Consumer-defined bias; finite samples copied unchanged, invalid samples become zero.";
            if (type == "WindOutputNode")
                return name == "Sway Phase" ? "Fraction of sway cycle; clamped 0..1." : "Dimensionless sway amplitude; scaled and clamped 0..1.";
            if (type == "TextureOutputNode" || type == "GrassOutputNode" || type == "HolesOutputNode")
                return "Dimensionless mask/weight; the terminal consumer applies its density or threshold.";
            if (type == "BiomeSplatOutputNode" && name == "Biome") return "Biome index / (BiomeCount - 1); categorical encoding, then softened for blending.";
            if (name == "Warp X" || name == "Warp Z") return "Dimensionless displacement encoded around 0.5; Domain Warp Apply converts (value - 0.5) * 2 * intensity to metres.";
            if (type == "PrevailingWindNode") return "Signed component encoded in 0..1 around 0.5; ToSigned converts using strength in metres per second.";
            if (output && type == "LithologyOutput")
            {
                if (name == "Unit Index") return "Zero-based geological unit index; -1 above the column.";
                if (name == "Cover Depth") return "Metres of surficial cover.";
                if (name == "Grain Size") return "Grain-size phi scale.";
                if (name == "Erodibility") return "Dimensionless erodibility.";
            }
            if (output && type == "MicroReliefOutput")
                return name == "Offset (mm)" ? "Signed millimetres of micro-relief." : "Dominant relief-process identifier.";
            if (name == "Plate Id") return "Dimensionless plate identifier from the plate noise hash.";
            if (name == "Rock Type") return "Rock type index / (cells - 1); zero for a single type.";
            if (name == "Basin Id") return "Dimensionless basin identifier; normalized or hash-scrambled, not elevation.";
            if (name == "Basin Size") return "Basin cell count / largest basin cell count.";
            if (type == "EcologyNode" && output)
            {
                if (name == "Zone Id") return "Landform zone index / (ZoneCount - 1); categorical encoding.";
                if (name == "Wetness Index") return "Topographic wetness index / wetnessIndexCeiling, clamped 0..1.";
                if (name == "Aridity") return "Precipitation / potential evapotranspiration / aridityCeiling, clamped 0..1.";
                return "Dimensionless environmental fraction or zone mask, 0..1.";
            }
            if (name == "Depth" && output && (type == "RiverFillNode" || type == "LakeFillNode" || type == "OceanLevelNode"))
                return "Water depth in normalized height / depthNormalise, clamped 0..1; not metres or absolute elevation.";
            if (name == "Water Level") return "Normalized absolute water elevation in the height field's datum; dry samples may contain ground height.";
            if (name == "Mask" || name.EndsWith(" Mask", StringComparison.Ordinal) || name == "Sources" ||
                name == "Precipitation" || name == "Boundary" || name == "Divides" || name == "Density" ||
                name == "Shoreline" || name == "Wetness" || name == "Override" || name == "Blend")
                return "Dimensionless mask/weight, normally 0..1; Matrix wires do not enforce bounds.";
            if (name == "A" || name == "B" || name == "Inputs" || type == "MathOperationsNode" ||
                node is CombinerNode || type == "CombineNode" || type == "SharedValueNode")
                return "Input field units; arithmetic/blending does not convert units (products/ratios depend on the operation).";
            if (name == "Height") return "Normalized height field; metres use the field's worldHeight and datum. Ports do not clamp values.";
            return "Unreviewed — inspect this port's calculation and consumer.";
        }

        // UC-T03-03: use reviewed declarations/consumption, never infer metres from a generic field name.
        // Unreviewed numerical units stay explicit so a partial catalog cannot pass as complete acceptance.
        private static string ReviewedControlUnits(FieldInfo field)
        {
            if (field.FieldType == typeof(bool)) return "toggle";
            if (field.FieldType.IsEnum) return "choice";
            if (field.FieldType == typeof(string)) return "text";
            if (field.FieldType == typeof(Color)) return "RGBA colour components";
            if (typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType)) return "asset reference";
            switch (field.DeclaringType.Name)
            {
                case "WorldGraphInputNode":
                    if (field.Name == "shoreSearchMetres") return "horizontal world metres; signed-distance search/clamp envelope, at most 256"; // UC-T07-11.
                    break;
                case "RiverCourseInputNode":
                    if (field.Name == "bankSlope") return "metres rise / metre run";
                    if (field.Name == "edgeFeatherMetres" || field.Name == "fallbackHalfWidthMetres" || field.Name == "bankReachMetres") return "world metres";
                    break;
                case "DomainWarpApplyNode":
                    if (field.Name == "intensity") return "world metres";
                    break;
                case "DomainWarpFieldNode":
                case "DomainWarpNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "octaves": return "octaves";
                        case "size": case "intensity": case "strength": case "epsilon": return "world metres";
                        case "detail": return "amplitude fraction per octave";
                    }
                    break;
                case "NoiseCreatorBase":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "octaves": return "octaves";
                        case "size": case "worldOffset": return "world metres";
                        case "detail": return "amplitude fraction per octave";
                        case "turbulence": case "intensity": return "dimensionless multiplier";
                        case "offset": return "normalized output units";
                    }
                    break;
                case "ConstantNode":
                    if (field.Name == "value") return "normalized field value";
                    break;
                case "ImportHeightmapNode": case "RealWorldImportNode":
                    switch (field.Name)
                    {
                        case "worldScale": return "world metres per texture repeat";
                        case "worldOffset": case "boundsMin": case "boundsMax": return "world metres (XZ)";
                        case "heightScale": return "dimensionless height multiplier";
                        case "offset": case "heightOffset": return "normalized height";
                    }
                    break;
                case "SimpleFormNode":
                    if (field.Name == "center" || field.Name == "radius") return "world metres";
                    if (field.Name == "height") return "normalized height";
                    if (field.Name == "falloff") return "fraction of radius";
                    break;
                case "MarbleNoiseNode":
                    if (field.Name == "bandFrequency") return "band repeats per noise period";
                    if (field.Name == "distortion") return "dimensionless displacement multiplier";
                    break;
                case "CausticNoiseNode": case "RidgedNoiseNode":
                    if (field.Name == "sharpness") return "dimensionless exponent";
                    if (field.Name == "weightFalloff") return "dimensionless octave-weight gain";
                    break;
                case "SwissNoiseNode":
                    if (field.Name == "warp") return "dimensionless gradient-warp gain";
                    if (field.Name == "lacunarity") return "frequency ratio per octave";
                    break;
                case "RidgedMultifractalNode": case "HybridMultifractalNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "size": return "world metres";
                        case "octaves": case "bands": return "octaves";
                        case "roughness": return field.DeclaringType.Name == "HybridMultifractalNode" ? "dimensionless fractal exponent H" : "amplitude fraction per octave";
                        case "sharpness": return "dimensionless exponent";
                        case "weightFalloff": return "dimensionless octave-weight gain";
                        case "lacunarity": return "frequency ratio per octave";
                        case "amplitude": return "dimensionless height multiplier";
                        case "offset": return "normalized potential/height units";
                    }
                    break;
                case "VoronoiNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "cellSize": case "worldOffset": return "world metres";
                        case "uniformity": return "dimensionless jitter blend";
                        case "octaves": return "octaves";
                        case "detail": return "amplitude fraction per octave";
                        case "intensity": return "dimensionless height multiplier";
                        case "offset": return "normalized height";
                    }
                    break;
                case "TectonicPlatesNode": case "TectonicPlateSeedNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "plateSize": return "world metres";
                        case "oceanFraction": return "fraction of plates";
                        case "plateRelief": case "ridgeHeight": case "convergentUplift": case "divergentRift": case "trenchDepth": return "normalized height";
                        case "ridgeWidth": case "marginWidth": return "fraction of plate size";
                        case "borderWarp": return "dimensionless border-warp gain";
                    }
                    break;
                case "ContinentalShapeMaskNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "size": return "world metres";
                        case "landRadius": return "fraction of world radius";
                        case "falloff": return "dimensionless exponent";
                        case "coastNoise": return "dimensionless coastline variation";
                        case "seaLevel": return "normalized land/sea threshold";
                    }
                    break;
                case "LatitudeGradientNode":
                    if (field.Name == "range" || field.Name == "equatorOffset") return "degrees latitude";
                    break;
                case "PrevailingWindNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "belts": return "circulation cells per hemisphere";
                        case "range": return "degrees latitude";
                        case "strength": return "metres per second";
                        case "variance": return "dimensionless variation";
                    }
                    break;
                case "RiverSourceSeedNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "density": return "sources per square kilometre";
                        case "minElevation": return "normalized height";
                        case "minSpacing": case "markRadius": return "world metres";
                        case "rainWeight": return "dimensionless weighting";
                    }
                    break;
                case "RandomSeedNode":
                    if (field.Name == "seed" || field.Name == "variant") return "deterministic seed component";
                    break;
                case "CurlNoiseNode":
                    if (field.Name == "epsilon") return "world metres";
                    break;
                case "WorldEdgeFalloffNode":
                    if (field.Name == "falloffMeters") return "world metres";
                    if (field.Name == "edgeHeight") return "normalized height";
                    if (field.Name == "curvePower") return "dimensionless exponent";
                    break;
                case "ThermalErosionNode":
                    if (field.Name == "iterations") return "passes";
                    if (field.Name == "talusAngle") return "degrees";
                    if (field.Name == "amount") return "fraction of excess material per pass";
                    if (field.Name == "relax") return "pixel sigma";
                    break;
                case "HydraulicErosionNode": case "SedimentMapNode":
                    switch (field.Name)
                    {
                        case "iterations": case "fluidityIterations": return "passes";
                        case "durability": case "sedimentAmount": return "dimensionless fraction";
                        case "erosionAmount": case "intensity": return "dimensionless multiplier";
                        case "relax": return "pixel sigma";
                        case "seaLevel": return "normalized height";
                        case "dropletCount": return "droplets";
                        case "dropletBrushRadius": return "lattice cells";
                        case "dropletCoarseFactor": return "resolution divisor";
                        case "dropletSeed": return "deterministic seed";
                    }
                    break;
                case "BlurNode": case "SharpenNode":
                    if (field.Name == "radius") return "lattice cells";
                    if (field.Name == "intensity") return "dimensionless multiplier";
                    break;
                case "TerraceNode":
                    if (field.Name == "steps") return "height levels";
                    if (field.Name == "edgeSoftness") return "fraction of one terrace interval";
                    break;
                case "PowerNode":
                    if (field.Name == "exponent") return "dimensionless exponent";
                    break;
                case "LevelsNode":
                    if (field.Name == "gamma") return "dimensionless inverse exponent";
                    if (field.Name == "inputBlack" || field.Name == "inputWhite" || field.Name == "outputBlack" || field.Name == "outputWhite") return "normalized field value";
                    break;
                case "ClampNode":
                    if (field.Name == "min" || field.Name == "max") return "input field units";
                    break;
                case "ContrastNode":
                    if (field.Name == "contrast") return "dimensionless amount (0.5 is neutral)";
                    if (field.Name == "pivot") return "normalized field value";
                    break;
                case "ParallaxNode":
                    if (field.Name == "viewDirection") return "dimensionless XZ direction";
                    if (field.Name == "strength") return "fraction of evaluated grid width per unit height";
                    break;
                case "DistortNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "size": return "world metres";
                        case "strength": return "fraction of evaluated grid width";
                        case "octaves": return "octaves";
                        case "detail": return "amplitude fraction per octave";
                    }
                    break;
                case "LedgeNode":
                    if (field.Name == "threshold") return "normalized height per lattice cell";
                    if (field.Name == "depth") return "normalized height";
                    break;
                case "CoastalErosionNode":
                    if (field.Name == "seaLevel" || field.Name == "waveHeight") return "normalized height";
                    if (field.Name == "strength") return "dimensionless fraction";
                    break;
                case "LithologyErosionNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "strength": return "dimensionless fraction";
                        case "reliefRadiusPx": return "lattice cells";
                        case "contactBlendM": return "world metres";
                    }
                    break;
                case "TextureOutputNode":
                    if (field.Name == "layerIndex0" || field.Name == "layerIndex1" || field.Name == "layerIndex2" || field.Name == "layerIndex3") return "zero-based terrain layer index (currently unused)";
                    break;
                case "HydrologyNode":
                    switch (field.Name)
                    {
                        case "drainageResolution": return "whole-world solve cells per side";
                        case "targetLakeCount": return "lakes (zero selects coverage)";
                        case "lakeChunkPercentage": case "lakeOutletChunkPercentage": return "fraction of world chunks";
                        case "lakeDistribution": return "LakeDistributionSettings: scaleToWorld; referenceWorldAreaM2 square metres; referenceLakeCount 0..4096; absoluteAreas; maximumAreaSquareMetres (zero unrestricted)."; // UC-T03-08.
                        case "minimumAreaSquareMetres": return "square metres"; // UC-T03-08.
                        case "seaLevel": case "minLakeDepth": case "depthNormalise": return "normalized height";
                        case "shoreSoftness": return "dimensionless fraction";
                    }
                    break;
                case "TextureImportMaskNode":
                    switch (field.Name)
                    {
                        case "worldScale": return "world metres per texture repeat";
                        case "worldOffset": return "world metres (XZ)";
                        case "intensity": return "dimensionless mask multiplier";
                        case "offset": return "normalized mask value";
                    }
                    break;
                case "HeightSelectorNode":
                    if (field.Name == "minHeight" || field.Name == "maxHeight" || field.Name == "falloff") return "normalized height (Map) or world metres (World), selected by units";
                    break;
                case "SlopeSelectorNode":
                    if (field.Name == "minAngle" || field.Name == "maxAngle" || field.Name == "falloff") return "degrees slope";
                    break;
                case "SlopeToDensityNode":
                    if (field.Name == "minSlopeDeg" || field.Name == "maxSlopeDeg" || field.Name == "falloffDeg") return "degrees slope";
                    break;
                case "AspectSelectorNode":
                    if (field.Name == "direction" || field.Name == "spread") return "degrees bearing";
                    break;
                case "DistanceFieldSelectorNode":
                    if (field.Name == "threshold") return "normalized source-mask value";
                    if (field.Name == "maxDistance") return "lattice cells (Pixels) or world metres (World), selected by units";
                    break;
                case "CurvatureSelectorNode":
                    if (field.Name == "threshold") return "normalized height difference from four-neighbour mean";
                    break;
                case "CavitySelectorNode": case "FlowMapSelectorNode":
                    if (field.Name == "intensity") return "dimensionless multiplier";
                    break;
                case "NoiseMaskSelectorNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "size": return "world metres";
                        case "octaves": return "octaves";
                        case "detail": return "amplitude fraction per octave";
                        case "intensity": return "dimensionless mask multiplier";
                        case "offset": return "normalized mask value";
                    }
                    break;
                case "RiverSeedCourseNode":
                    switch (field.Name)
                    {
                        case "solveResolution": return "whole-world solve cells per side";
                        case "seaLevel": case "channelDepth": return "normalized height";
                        case "sourceThreshold": return "normalized seed-mask value";
                        case "maxRivers": return "rivers";
                        case "minSourceSpacingMetres": case "channelHalfWidthMetres": case "floodplainMetres": return "world metres";
                        case "mouthWidthMultiplier": case "mouthDepthMultiplier": return "dimensionless multiplier";
                        case "uphillPenalty": return "extra metres of search cost per metre of climb"; // UC-T03-04: physical routing costs.
                        case "maxSearchVisits": return "settled cells per source; zero allows the entire solve lattice";
                        case "profile": return "RiverProfileCurves: enabled toggle; width/depth base/min in metres, velocity base/min in metres per second; dimensionless curve multipliers over source-to-mouth fraction 0..1.";
                    }
                    break;
                case "RiverNetworkNode":
                    switch (field.Name)
                    {
                        case "hierarchyTargets": return "RiverHierarchyTargets: enabled; trunk/river counts 0..4096; streams per river 0..64; referenceAreaM2 square metres; minimumSeparationMetres world metres; fractional count tolerance 0..1."; // UC-T03-05.
                        case "solveResolution": return "whole-world solve cells per side";
                        case "seaLevel": case "lakeBasinDepth": case "flatRoutingEpsilon": return "normalized height";
                        case "trunkChannelWidthMetres": case "minTrenchDepthMetres": return "world metres";
                        case "channelInitiationFraction": return "fraction of world area";
                        case "minDrainageAreaM2": case "slopeAreaThreshold": return "square metres";
                        case "slopeExponent": return "dimensionless exponent";
                        case "minReachCells": return "solve cells";
                        case "maximumLakeOutletSources": return "valid lake-source channels; -1 means all, zero means none"; // UC-T03-07.
                        case "runoffMetresPerSecond": return "metres per second";
                        case "floodplainWidthMultiple": case "depthScale": case "trunkAreaMultiple": case "naturalRuggedness": return "dimensionless multiplier";
                        case "floodplainDepthFraction": return "fraction of channel depth";
                        case "minOrderToCarve": return "Strahler order";
                        case "naturalSeed": return "deterministic seed";
                    }
                    break;
                case "RiverBanksNode":
                    if (field.Name == "bankEase") return "normalized height";
                    if (field.Name == "bankBandMetres" || field.Name == "floodplainMetres" || field.Name == "channelHalfWidthMetres") return "world metres";
                    break;
                case "RiverFillNode": case "LakeFillNode": case "OceanLevelNode":
                    switch (field.Name)
                    {
                        case "drainageResolution": return "whole-world solve cells per side";
                        case "bankFreeboard": case "minDepth": case "depthNormalise": case "seaLevel": case "shorelineBand": case "edgeSoftness": return "normalized height";
                        case "channelThreshold": return "normalized channel-mask value";
                        case "shoreSoftness": return "dimensionless mask-edge softness";
                        case "minAreaSquareMetres": return "square metres";
                        case "lakeDistribution": return "LakeDistributionSettings: scaleToWorld; referenceWorldAreaM2 square metres; referenceLakeCount 0..4096; absoluteAreas; maximumAreaSquareMetres (zero unrestricted)."; // UC-T03-08.
                    }
                    break;
                case "LakeBasinNode":
                    switch (field.Name)
                    {
                        case "maxDepth": case "ruggedness": return "normalized height";
                        case "shoreRunMetres": case "ruggednessFeatureMetres": return "world metres";
                        case "shelfFraction": return "dimensionless basin-profile shelf fraction";
                        case "ruggednessOctaves": return "octaves";
                        case "ruggednessPersistence": return "amplitude fraction per octave";
                        case "seed": return "deterministic seed";
                    }
                    break;
                case "WaterBankSeatNode":
                    if (field.Name == "bankSlope") return "metres rise / metre run";
                    if (field.Name == "benchWidth" || field.Name == "maxRaiseMetres" || field.Name == "skirtWidth") return "world metres";
                    if (field.Name == "minDepth") return "normalized height";
                    if (field.Name == "strength") return "dimensionless blend";
                    break;
                case "WatershedBasinSegmentNode":
                    if (field.Name == "drainageResolution") return "whole-world solve cells per side";
                    if (field.Name == "minBasinFraction") return "fraction of world area";
                    if (field.Name == "seed") return "deterministic seed";
                    break;
                case "GroundwaterWetlandMaskNode":
                    switch (field.Name)
                    {
                        case "drainageResolution": return "whole-world solve cells per side";
                        case "maxSlope": return "degrees slope";
                        case "saturationArea": return "square metres";
                        case "waterProximity": return "world metres";
                        case "tableDepth": return "normalized height";
                        case "edgeNoise": return "dimensionless variation";
                        case "seed": return "deterministic seed";
                    }
                    break;
                case "UnderwaterTerrainExtendNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "drainageResolution": return "whole-world solve cells per side";
                        case "seaLevel": case "canyonDepth": case "seamountHeight": case "sedimentDetail": return "normalized height";
                        case "canyonCatchmentArea": return "square metres";
                        case "canyonReach": return "fraction of submarine range";
                        case "seamountSpacing": return "world metres";
                    }
                    break;
                case "SwampBasinNode":
                    switch (field.Name)
                    {
                        case "swampLevel": case "floorDrop": case "bandBelow": case "bandAbove": case "hummockAmp": return "normalized height";
                        case "flatten": return "dimensionless blend";
                        case "detectMaxSlope": return "normalized height per lattice cell";
                        case "hummockFeatureMetres": return "world metres";
                        case "seed": return "deterministic seed";
                    }
                    break;
                case "SkyOcclusionNode":
                    switch (field.Name)
                    {
                        case "directions": return "horizon directions";
                        case "reachPixels": return "lattice cells";
                        case "strength": return "dimensionless slope multiplier";
                        case "minVisibility": return "visibility fraction";
                    }
                    break;
                case "NormalizeNode":
                    if (field.Name == "newMin" || field.Name == "newMax" || field.Name == "sourceMin" || field.Name == "sourceMax") return "input/output field units";
                    break;
                case "MinimumNode": case "MaximumNode":
                    if (field.Name == "smoothness") return "input field units";
                    break;
                case "WeightedAverageNode":
                    if (field.Name == "weightA" || field.Name == "weightB") return "relative dimensionless weight";
                    break;
                case "BlendNode":
                    if (field.Name == "opacity" || field.Name == "weight") return "dimensionless blend";
                    break;
                case "SharedValueNode":
                    if (field.Name == "value") return "scalar field value";
                    break;
                case "MathOperationsNode":
                    if (field.Name == "constant") return "operation-dependent scalar replacing input B";
                    break;
                case "HeightOutputNode":
                    switch (field.Name)
                    {
                        case "heightScale": case "seaFreeboardMetres": case "continentalRiseMetres": case "continentalRiseDistanceMetres":
                        case "shoreGuardDeadzoneMetres": case "coastalRange": case "beachFlat": case "coastalPlainMetres": return "world metres";
                        case "seaLevel": case "coastGrade": case "basinDepth": case "coastalRise": case "cliffThreshold": return "normalized height";
                        case "floorStrength": case "continentalRiseStrength": case "coastalVariety": case "coastalShelfStrength": return "dimensionless blend";
                        case "coastalRangeFraction": case "beachFlatFraction": case "coastalPlainFraction": return "fraction of shorter world span";
                    }
                    break;
                case "HolesOutputNode":
                    if (field.Name == "threshold") return "normalized mask value";
                    break;
                case "NormalMapOutputNode":
                    if (field.Name == "strength") return "dimensionless gradient multiplier";
                    break;
                case "GrassOutputNode":
                    if (field.Name == "detailLayerIndex") return "zero-based terrain detail prototype index";
                    if (field.Name == "maxDensityPerCell") return "density units per detail cell";
                    break;
                case "CustomMeshOutputNode":
                    if (field.Name == "heightScale") return "world metres";
                    break;
                case "LithologyOutput": case "MicroReliefOutput":
                    if (field.Name == "seed") return "deterministic seed";
                    if (field.Name == "contactBlendM") return "world metres";
                    break;
                case "DropletErosionNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "raindrops": return "droplets";
                        case "maxLifetime": return "simulation steps";
                        case "inertia": return "velocity fraction retained per step";
                        case "capacity": return "sediment capacity coefficient in model units";
                        case "erodeRate": case "depositRate": case "evaporation": return "fraction per simulation step";
                        case "gravity": return "acceleration coefficient in model units";
                        case "erosionRadius": return "lattice cells";
                        case "minSlope": return "normalized height drop per simulation step";
                    }
                    break;
                case "GlacialCarveNode":
                    switch (field.Name)
                    {
                        case "snowline": case "snowlineFalloff": case "carveDepth": return "normalized height";
                        case "valleyWidth": return "lattice cells";
                        case "uShape": return "dimensionless profile blend";
                        case "flowThreshold": return "fraction of the evaluated field's maximum flow";
                        case "iterations": return "passes";
                    }
                    break;
                case "SlopeRemapCurveNode":
                    if (field.Name == "slopeCurve") return "input/output slope fraction (0..1 represents 0..90 degrees)";
                    if (field.Name == "strength") return "dimensionless blend";
                    if (field.Name == "iterations") return "solver passes";
                    break;
                case "CurveNode":
                    if (field.Name == "curve") return "input field value to output field value";
                    break;
                case "FaultLineDisplacementNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "displacement": return "normalized height";
                        case "gradient": case "faultSpacing": case "strikeSlip": return "world metres";
                        case "traceWarp": return "dimensionless variation";
                    }
                    break;
                case "CoastalShelfShaperNode":
                    switch (field.Name)
                    {
                        case "seaLevel": case "shelfDepth": case "abyssalDepth": return "normalized height";
                        case "shelfExtent": return "fraction of submarine range";
                        case "slopeSharpness": return "dimensionless exponent";
                        case "reliefRetention": return "dimensionless blend";
                    }
                    break;
                case "WindOutputNode":
                    if (field.Name == "amplitudeScale") return "dimensionless amplitude multiplier";
                    if (field.Name == "minimumAmplitude") return "normalized amplitude";
                    break;
                case "EcologyNode":
                    if (field.Name == "wetnessIndexCeiling") return "topographic wetness index";
                    if (field.Name == "aridityCeiling") return "precipitation / potential evapotranspiration ratio";
                    if (field.Name == "sampleStride") return "lattice cells between environment samples";
                    break;
                case "WindErosionNode":
                    switch (field.Name)
                    {
                        case "direction": return "degrees bearing";
                        case "windSpeed": return "model speed coefficient (pickup uses its square)";
                        case "strength": return "dimensionless pickup multiplier";
                        case "carryDistance": return "lattice cells";
                        case "iterations": return "passes";
                    }
                    break;
                case "CommentGroupNode":
                    if (field.Name == "size") return "graph canvas units";
                    break;
                case "VoronoiRockStrataNode":
                    switch (field.Name)
                    {
                        case "seed": return "deterministic seed";
                        case "cells": return "rock types";
                        case "bedThickness": return "fraction of world height";
                        case "dipAngle": case "dipDirection": return "degrees";
                        case "domainSize": return "world metres";
                        case "stepStrength": case "hardnessVariation": return "dimensionless fraction";
                    }
                    break;
            }
            // UC-T03-03: structured controls retain their per-member units, including mixed-unit records.
            switch (field.DeclaringType.Name)
            {
                case "SpotBrushNode":
                    if (field.Name == "spots") return "BrushSpot list: position XZ/radius in world metres; height in normalized height; falloff fraction 0..1.";
                    break;
                case "CompositeFilterNode":
                    string unit = field.Name == "height" ? "normalized height" : field.Name == "mask" ? "dimensionless mask" : "degrees";
                    return "RangeFilter: min/max/falloff in " + unit + "; enabled/invert/graded toggles; graded ignores falloff.";
                case "BiomeSplatOutputNode":
                    switch (field.Name)
                    {
                        case "layerCount": return "terrain layers";
                        case "defaultLayer": case "wetLayer": case "beachLayer": case "bareLayer": case "snowLayer": case "overrideLayer": return "zero-based terrain layer index";
                        case "biomeSoftening": return "Gaussian sigma in lattice samples";
                        case "biomeBlendNoise": case "biomeBlendNoiseScaleM": case "lakeBareMetres": case "riverBareMetres": case "oceanBareMetres": return "world metres";
                        case "snowTileSize": return "world metres per texture repeat";
                        case "contrast": return "dimensionless weight exponent";
                        case "wetThreshold": return "dimensionless wetness threshold";
                        case "wetStrength": return "dimensionless weight multiplier";
                        case "seaLevel": case "beachHeight": case "snowLine": case "snowBlend": return "normalized height";
                        case "beachMaxSlope": case "snowMaxSlope": case "snowSlopeBlend": return "degrees slope";
                        case "snowSmoothness": return "dimensionless smoothness 0..1";
                        case "rules": return "BiomeSplatRule list: biome choice; ground/slope/high layer indices; slopeStart/End degrees; highStart/End normalized height; weight multiplier.";
                        case "layerTextures": return "SplatLayerTexture list: layer index, display label, borrowed albedo/normal/AO assets, RGBA tint, tileSizeMetres, smoothness 0..1 and normalScale multiplier.";
                    }
                    break;
            }
            return "Unreviewed — inspect this control's calculation.";
        }

        private static string ControlCondition(FieldInfo field)
        {
            if (field.DeclaringType == typeof(Nodes.Selectors.RiverNetworkNode) && field.Name == "hierarchyTargets")
                return "enabled; selects connected courses from existing thresholds. scaleToWorld scales counts by area and head spacing by length; false preserves authored counts/metres. Results include infeasibility diagnostics."; // UC-T03-05.
            if (field.DeclaringType == typeof(Nodes.Selectors.RiverNetworkNode) && field.Name == "maximumLakeOutletSources")
                return "generateLakeOutlets; valid downhill spill routes only, highest discharge first. Hierarchy/order selection may reject admitted sources."; // UC-T03-07.
            // UC-T03-03: distinguish helper-only settings and optional consumers from ineffective graph controls.
            if (field.DeclaringType == typeof(Nodes.Creators.PrevailingWindNode) && field.Name == "strength")
                return "ToSigned helper only; generated Wind X/Z stay normalized and do not change with this setting.";
            if (field.DeclaringType == typeof(Nodes.Selectors.RiverSeedCourseNode) && field.Name == "profile")
                return "Enabled and main-thread baked width/depth replace scalar taper; velocity is not consumed by this terrain node.";
            if (field.DeclaringType == typeof(Nodes.Outputs.BiomeSplatOutputNode))
            {
                switch (field.Name)
                {
                    case "rules": return "No populated prepared palette; otherwise palette rules take precedence.";
                    case "biomeSoftening": return "Biome connected and sigma > 0.01 lattice samples.";
                    case "biomeBlendNoise": case "biomeBlendNoiseScaleM": return "Biome connected and biomeBlendNoise > 0.001 metres.";
                    case "wetLayer": case "wetThreshold": case "wetStrength": return "Wetness connected and wetStrength > 0.";
                    case "overrideLayer": return "Override connected; applied after procedural rules.";
                    case "beachLayer": case "beachHeight": case "beachMaxSlope": return "beachEnabled and eligible height/slope.";
                    case "bareLayer": case "lakeBareMetres": case "riverBareMetres": case "oceanBareMetres": return "waterBareEnabled and captured water coverage/proximity is available.";
                }
                if (field.Name.StartsWith("snow", StringComparison.Ordinal) && field.Name != "snowEnabled")
                    return "snowEnabled; weights require eligible height/slope, textures describe the snow layer.";
            }
            if (field.DeclaringType == typeof(Nodes.Outputs.TextureOutputNode) && field.Name.StartsWith("layerIndex", StringComparison.Ordinal))
                return "Currently unused; the writer maps input 0..3 to output layers 0..3. Review mapping in Topic 7.";
            if (field.DeclaringType == typeof(Nodes.Creators.HydrologyNode))
            {
                if (field.Name == "lakeOutletChunkPercentage") return "Currently unused; directed lake outflows remain UC-T03-07.";
                if (field.Name == "targetLakeCount") return "Positive count caps eligible masks and native lake generation; zero uses automatic distribution or legacy coverage."; // UC-T03-08.
                if (field.Name == "lakeChunkPercentage" || field.Name == "generateOcean")
                    return "Consumed by HydrologyGenerator scene reconciliation; does not change this node's numerical outputs.";
            }
            if (field.Name == "lakeDistribution" && (field.DeclaringType == typeof(Nodes.Creators.HydrologyNode) || field.DeclaringType == typeof(Nodes.Selectors.LakeFillNode)))
                return "scaleToWorld resolves count by world area; absoluteAreas preserves authored area limits. Only existing depth/area-eligible pools are selected."; // UC-T03-08.
            if (field.DeclaringType == typeof(Nodes.Selectors.RiverNetworkNode) &&
                (field.Name == "minDrainageAreaM2" || field.Name == "slopeAreaThreshold"))
                return "scaleToWorld == false; otherwise derived from world area/slope";
            if (field.DeclaringType == typeof(Nodes.Selectors.SlopeToDensityNode) && field.Name == "falloffDeg")
                return "graded == false; the graded mode uses its own continuous band";
            if (field.DeclaringType == typeof(Nodes.Creators.DomainWarpFieldNode) && field.Name == "intensity")
                return "recursive == true; final displacement belongs to Domain Warp Apply";
            if (field.DeclaringType == typeof(Nodes.Creators.RiverCourseInputNode))
            {
                if (field.Name == "bankSlope" || field.Name == "bankReachMetres") return "carveTrench == true and captured bed/depth data exists";
                if (field.Name == "fallbackHalfWidthMetres") return "captured course has a missing or nonfinite endpoint width";
            }
            return "Input-dependent; see the node description and effect fixture.";
        }

        private static string DefaultDescription(object value)
        {
            if (value == null) return "null";
            if (value is string text) return text;
            if (value is IFormattable number) return number.ToString(null, CultureInfo.InvariantCulture);
            if (value is bool flag) return flag ? "true" : "false";
            if (value is Array array) return array.GetType().Name + "[" + array.Length + "]";
            // Avoid native object properties and arbitrary plugin ToString implementations.
            return value.GetType().FullName + " (see serialized defaults)";
        }
    }
}
