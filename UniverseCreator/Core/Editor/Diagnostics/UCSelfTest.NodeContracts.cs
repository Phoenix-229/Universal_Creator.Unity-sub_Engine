// UC-T03-03: registered Node Graph inventory and specialized modifier behavior.
using System;
using UnityEngine;
using UniverseCreator.Editor.Utilities;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Modifiers;
using UniverseCreator.Nodes.Selectors;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        [Serializable]
        private sealed class ContractHeightFeed : CreatorNode
        {
            public bool trench, warp;
            public float constant = 0.8f;
            public override string NodeName => "Contract field fixture";
            public override NodeCategory Category => NodeCategory.Creator;
            protected override void Create(Matrix matrix, TileData tile, StopToken stop)
            {
                Matrix reference = trench ? matrix.Blank() : null;
                Vector2 pixelSize = matrix.PixelSize; // UC-T03-03: the fixture must agree across a modifier's padded and unpadded pulls.
                for (int z = 0; z < matrix.rect.size.z; z++)
                {
                    stop?.ThrowIfStopRequested();
                    for (int x = 0; x < matrix.rect.size.x; x++)
                    {
                        int i = z * matrix.rect.size.x + x;
                        float wx = matrix.worldPos.x + x * pixelSize.x, wz = matrix.worldPos.z + z * pixelSize.y;
                        float ground = 0.42f + 0.025f * Mathf.Sin(wx * 0.07f) + 0.012f * Mathf.Cos(wz * 0.04f);
                        matrix.arr[i] = warp ? constant : ground;
                        if (reference != null)
                        {
                            reference.arr[i] = ground;
                            float delta = (wx - 165f) * 0.1f;
                            matrix.arr[i] -= 0.02f * Mathf.Exp(-delta * delta / 12f);
                        }
                    }
                }
                matrix.trenchReference = reference;
            }
        }

        private static void TestGroundNodeContracts()
        {
            // UC-T03-03: external integrations can bind the original public routing signature.
            Func<Generator, int, TileData, int, float, bool, StopToken, WorldDrainage> routing = WorldDrainage.Solve;
            Check(routing(null, 0, null, 32, 0f, false, null) == null,
                "UC-T03-03 original drainage method-group API retains its missing-input contract.");
            NodeCatalog.GroundContract[] contracts = NodeCatalog.CaptureGroundContracts();
            Check(contracts.Length > 0, "UC-T03-03 shipped ground/water node contract inventory is populated.");
            int controls = 0, undeclared = 0, unreviewedUnits = 0, unreviewedPorts = 0, inactive = 0;
            foreach (NodeCatalog.GroundContract contract in contracts)
            {
                bool valid = contract.inputs != null && contract.outputs != null && !string.IsNullOrEmpty(contract.defaults);
                // UC-T03-03: unit metadata must track port order and newly discovered nodes must not silently escape review.
                valid &= contract.inputUnits.Length == contract.inputs.Length && contract.outputUnits.Length == contract.outputs.Length &&
                    !string.IsNullOrEmpty(contract.preparation) && !string.IsNullOrEmpty(contract.publication);
                foreach (string unit in contract.inputUnits) if (unit.StartsWith("Unreviewed", StringComparison.Ordinal)) unreviewedPorts++;
                foreach (string unit in contract.outputUnits) if (unit.StartsWith("Unreviewed", StringComparison.Ordinal)) unreviewedPorts++;
                foreach (Port port in contract.inputs) valid &= port.direction == PortDirection.Input && !string.IsNullOrEmpty(port.name);
                foreach (Port port in contract.outputs) valid &= port.direction == PortDirection.Output && !string.IsNullOrEmpty(port.name);
                foreach (NodeCatalog.ControlContract control in contract.controls)
                {
                    controls++;
                    if (control.description.StartsWith("No runtime", StringComparison.Ordinal)) undeclared++;
                    if (control.units.StartsWith("Unreviewed", StringComparison.Ordinal)) unreviewedUnits++;
                    if (control.activeWhen.StartsWith("Currently unused", StringComparison.Ordinal)) inactive++;
                    valid &= !control.hasMinimum || (!float.IsNaN(control.minimum) && !float.IsInfinity(control.minimum));
                    valid &= !control.hasMaximum || (!float.IsNaN(control.maximum) && !float.IsInfinity(control.maximum) && control.maximum >= control.minimum);
                }
                Check(valid, "UC-T03-03 " + contract.name + " exposes valid ports, serialized defaults and declared bounds.");
            }
            Debug.Log("UC-T03-03 contracts=" + contracts.Length + "; controls=" + controls +
                "; controlsWithoutRuntimeDescription=" + undeclared + "; controlsWithUnreviewedUnits=" + unreviewedUnits +
                "; portsWithUnreviewedUnits=" + unreviewedPorts + "; knownInactiveControls=" + inactive + "; control-effect audit remains separate.");
            Check(unreviewedUnits == 0 && unreviewedPorts == 0, "UC-T03-03 every shipped ground/water control and port has reviewed unit metadata.");
            TestSpecializedTrenchModifiers();
            TestWaterModifierTrenchReferences();
            TestManualCourseControlContracts();
            TestManualCourseInvalidControls(); // UC-T03-03: invalid settings, extreme finite data and bypass use the real evaluator.
            TestWarpFieldControlContract(contracts); // UC-T03-03: recursive intensity is conditional, with final displacement owned by the applier.
            TestCurlControlContracts(); // UC-T03-03: formerly unused fractal controls reach both production curl callers.
        }

        private delegate void OriginalCurlSampler(float x, float z, float size, out float cx, out float cz, float epsilon, int iterations);

        private static void TestCurlControlContracts()
        {
            var noise = new Noise(2718);
            OriginalCurlSampler original = noise.Curl2D;
            float oldX, oldZ, newX, newZ;
            original(81f, 39f, 50f, out oldX, out oldZ, 0.05f, 4);
            noise.Curl2D(81f, 39f, 50f, out newX, out newZ, 0.05f, 4, 0.5f, 0f);
            Check(oldX == newX && oldZ == newZ, "UC-T03-03 original Curl2D method-group signature preserves its default potential.");
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 33, 33), Vector3.zero, new Vector3(320f, 600f, 320f), 600f);
            try
            {
                var ground = new ContractHeightFeed { id = "curl-control-ground", trench = true };
                var low = new DomainWarpNode { id = "curl-low-detail", size = 40f, strength = 120f, octaves = 4, detail = 0f };
                var high = new DomainWarpNode { id = "curl-high-detail", size = 40f, strength = 120f, octaves = 4, detail = 1f };
                var below = new DomainWarpNode { id = "curl-below-detail", size = 40f, strength = 120f, octaves = 4, detail = -10f };
                var above = new DomainWarpNode { id = "curl-above-detail", size = 40f, strength = 120f, octaves = 4, detail = 10f };
                var invalid = new DomainWarpNode { id = "curl-invalid-detail", size = 40f, strength = 120f, octaves = 4, detail = float.NaN };
                var standard = new DomainWarpNode { id = "curl-default-detail", size = 40f, strength = 120f, octaves = 4, detail = 0.5f };
                var noStrength = new DomainWarpNode { id = "curl-invalid-strength", strength = float.NaN };
                var extreme = new DomainWarpNode { id = "curl-extreme-strength", strength = float.MaxValue };
                var disabled = new DomainWarpNode { id = "curl-disabled", disabled = true };
                var missing = new DomainWarpNode { id = "curl-missing-height" };
                graph.nodes.AddRange(new Generator[] { ground, low, high, below, above, invalid, standard, noStrength, extreme, disabled, missing });
                foreach (Generator node in new Generator[] { low, high, below, above, invalid, standard, noStrength, extreme, disabled })
                    graph.AddConnection(new Connection(ground.id, 0, node.id, 0));
                var curl = new CurlNoiseNode { id = "curl-creator-low", size = 40f, octaves = 4, detail = 0f };
                var curlDetail = new CurlNoiseNode { id = "curl-creator-high", size = 40f, octaves = 4, detail = 1f };
                var curlFold = new CurlNoiseNode { id = "curl-creator-fold", size = 40f, octaves = 4, detail = 0f, turbulence = 1f };
                var curlInvalid = new CurlNoiseNode { id = "curl-creator-invalid", size = 40f, octaves = 4, detail = float.NaN, turbulence = float.PositiveInfinity };
                var curlDefault = new CurlNoiseNode { id = "curl-creator-default", size = 40f, octaves = 4 };
                var curlDisabled = new CurlNoiseNode { id = "curl-creator-disabled", disabled = true };
                graph.nodes.AddRange(new Generator[] { curl, curlDetail, curlFold, curlInvalid, curlDefault, curlDisabled });
                graph.compiledPlan = CompiledGraphPlan.Build(graph);
                var evaluator = new GraphEvaluator(graph); tile.Bind(evaluator); var stop = new StopToken();
                Matrix source = evaluator.PullNode(ground, 0, tile, stop) as Matrix;
                Matrix lowField = evaluator.PullNode(low, 0, tile, stop) as Matrix;
                Matrix highField = evaluator.PullNode(high, 0, tile, stop) as Matrix;
                Matrix hugeField = evaluator.PullNode(extreme, 0, tile, stop) as Matrix;
                Matrix missingField = evaluator.PullNode(missing, 0, tile, stop) as Matrix;
                float effect = 0f;
                bool finite = true, empty = true;
                for (int i = 0; i < lowField.arr.Length; i++)
                {
                    effect = Mathf.Max(effect, Mathf.Abs(highField.arr[i] - lowField.arr[i]));
                    finite &= !float.IsNaN(hugeField.arr[i]) && !float.IsInfinity(hugeField.arr[i]);
                    empty &= missingField.arr[i] == 0f;
                }
                Check(effect > 1e-6f, "UC-T03-03 Domain Warp detail changes the produced terrain on a varying input with multiple octaves.");
                Check(FieldsIdentical(lowField.arr, ((Matrix)evaluator.PullNode(below, 0, tile, stop)).arr) &&
                    FieldsIdentical(highField.arr, ((Matrix)evaluator.PullNode(above, 0, tile, stop)).arr) &&
                    FieldsIdentical(((Matrix)evaluator.PullNode(standard, 0, tile, stop)).arr, ((Matrix)evaluator.PullNode(invalid, 0, tile, stop)).arr),
                    "UC-T03-03 Domain Warp detail clamps out-of-range values and defaults nonfinite values.");
                Check(finite && empty && ReferenceEquals(source, evaluator.PullNode(disabled, 0, tile, stop)) &&
                    FieldsIdentical(source.arr, ((Matrix)evaluator.PullNode(noStrength, 0, tile, stop)).arr),
                    "UC-T03-03 curl warp handles missing input, disabled bypass and nonfinite/extreme displacement.");
                Matrix curlField = evaluator.PullNode(curl, 0, tile, stop) as Matrix;
                Check(!FieldsIdentical(curlField.arr, ((Matrix)evaluator.PullNode(curlDetail, 0, tile, stop)).arr) &&
                    !FieldsIdentical(curlField.arr, ((Matrix)evaluator.PullNode(curlFold, 0, tile, stop)).arr),
                    "UC-T03-03 Curl Noise consumes its inherited detail and turbulence controls without requiring an input.");
                Check(FieldsIdentical(((Matrix)evaluator.PullNode(curlInvalid, 0, tile, stop)).arr,
                    ((Matrix)evaluator.PullNode(curlDefault, 0, tile, stop)).arr) && evaluator.PullNode(curlDisabled, 0, tile, stop) == null,
                    "UC-T03-03 Curl Noise defaults invalid fractal controls and produces no field when disabled.");
            }
            finally { tile.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(graph); }
        }

        private static void TestWarpFieldControlContract(NodeCatalog.GroundContract[] contracts)
        {
            var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 33, 33), Vector3.zero, new Vector3(320f, 600f, 320f), 600f);
            try
            {
                var field = new DomainWarpFieldNode { size = 70f, intensity = 0f };
                Matrix normal = field.Generate(0, tile, null) as Matrix;
                field.intensity = 120f;
                Matrix unchanged = field.Generate(0, tile, null) as Matrix;
                field.recursive = true;
                Matrix recursive = field.Generate(0, tile, null) as Matrix;
                float difference = 0f;
                for (int i = 0; i < normal.arr.Length; i++) difference = Mathf.Max(difference, Mathf.Abs(recursive.arr[i] - normal.arr[i]));
                Check(FieldsIdentical(normal.arr, unchanged.arr) && difference > 1e-4f,
                    "UC-T03-03 warp-field intensity affects recursive coordinates only; nonrecursive normalized offsets are unchanged.");
                bool documented = false;
                foreach (NodeCatalog.GroundContract contract in contracts)
                    if (contract.type == typeof(DomainWarpFieldNode).FullName)
                        foreach (NodeCatalog.ControlContract control in contract.controls)
                            if (control.field == typeof(DomainWarpFieldNode).FullName + ".intensity")
                                documented = control.units == "world metres" && control.activeWhen.StartsWith("recursive == true", StringComparison.Ordinal);
                Check(documented, "UC-T03-03 the executable catalog records the warp-field control's units and activation condition.");
            }
            finally { tile.ReleaseProductMemory(); }
        }

        private static void TestSpecializedTrenchModifiers()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 33, 33), Vector3.zero, new Vector3(320f, 600f, 320f), 600f);
            try
            {
                var carved = new ContractHeightFeed { id = "contract-carved", trench = true };
                var ground = new ContractHeightFeed { id = "contract-ground" };
                var warp = new ContractHeightFeed { id = "contract-warp", warp = true };
                var apply = new DomainWarpApplyNode { id = "contract-apply", intensity = 23f };
                var reference = new DomainWarpApplyNode { id = "contract-reference", intensity = 23f };
                var highQuality = new DomainWarpApplyNode { id = "contract-cubic", intensity = 23f, highQuality = true };
                var invalid = new DomainWarpApplyNode { id = "contract-invalid", intensity = float.NaN };
                var extreme = new DomainWarpApplyNode { id = "contract-extreme", intensity = float.MaxValue };
                var disabled = new DomainWarpApplyNode { id = "contract-disabled", disabled = true };
                var noWarp = new DomainWarpApplyNode { id = "contract-no-warp" };
                var noHeight = new DomainWarpApplyNode { id = "contract-no-height" };
                var strata = new VoronoiRockStrataNode { id = "contract-strata", seed = 81 };
                var strataReference = new VoronoiRockStrataNode { id = "contract-strata-reference", seed = 81 };
                graph.nodes.AddRange(new Generator[] { carved, ground, warp, apply, reference, highQuality, invalid, extreme, disabled, noWarp, noHeight, strata, strataReference });
                foreach (Generator node in new Generator[] { apply, highQuality, invalid, extreme, disabled })
                {
                    graph.AddConnection(new Connection(carved.id, 0, node.id, 0));
                    graph.AddConnection(new Connection(warp.id, 0, node.id, 1));
                }
                graph.AddConnection(new Connection(carved.id, 0, noWarp.id, 0));
                graph.AddConnection(new Connection(ground.id, 0, reference.id, 0));
                graph.AddConnection(new Connection(warp.id, 0, reference.id, 1));
                graph.AddConnection(new Connection(carved.id, 0, strata.id, 0));
                graph.AddConnection(new Connection(ground.id, 0, strataReference.id, 0));
                graph.compiledPlan = CompiledGraphPlan.Build(graph);
                var evaluator = new GraphEvaluator(graph); tile.Bind(evaluator); var stop = new StopToken();
                Matrix input = evaluator.PullNode(carved, 0, tile, stop) as Matrix;
                Matrix bilinear = evaluator.PullNode(apply, 0, tile, stop) as Matrix;
                Matrix baseWarp = evaluator.PullNode(reference, 0, tile, stop) as Matrix;
                Matrix cubic = evaluator.PullNode(highQuality, 0, tile, stop) as Matrix;
                Matrix invalidWarp = evaluator.PullNode(invalid, 0, tile, stop) as Matrix;
                Matrix extremeWarp = evaluator.PullNode(extreme, 0, tile, stop) as Matrix;
                Matrix bypassed = evaluator.PullNode(disabled, 0, tile, stop) as Matrix;
                Matrix unwarped = evaluator.PullNode(noWarp, 0, tile, stop) as Matrix;
                Matrix empty = evaluator.PullNode(noHeight, 0, tile, stop) as Matrix;
                Matrix rock = evaluator.PullNode(strata, 0, tile, stop) as Matrix;
                Matrix baseRock = evaluator.PullNode(strataReference, 0, tile, stop) as Matrix;
                float warpEffect = 0f, qualityEffect = 0f, invalidEffect = 0f, warpError = 0f, rockError = 0f;
                bool inputContracts = true;
                for (int i = 0; i < input.arr.Length; i++)
                {
                    warpEffect = Mathf.Max(warpEffect, Mathf.Abs(bilinear.arr[i] - input.arr[i]));
                    qualityEffect = Mathf.Max(qualityEffect, Mathf.Abs(cubic.arr[i] - bilinear.arr[i]));
                    invalidEffect = Mathf.Max(invalidEffect, Mathf.Abs(invalidWarp.arr[i] - input.arr[i]));
                    warpError = Mathf.Max(warpError, Mathf.Abs(bilinear.trenchReference.arr[i] - baseWarp.arr[i]));
                    rockError = Mathf.Max(rockError, Mathf.Abs(rock.trenchReference.arr[i] - baseRock.arr[i]));
                    inputContracts &= !float.IsNaN(extremeWarp.arr[i]) && !float.IsInfinity(extremeWarp.arr[i]) &&
                        bypassed.arr[i] == input.arr[i] && unwarped.arr[i] == input.arr[i] && empty.arr[i] == 0f;
                }
                Check(warpEffect > 0.001f && qualityEffect > 1e-6f, "UC-T03-03 warp displacement and interpolation quality both affect a curved input.");
                Check(invalidEffect < 1e-6f, "UC-T03-03 nonfinite warp displacement is neutral and finite.");
                Check(inputContracts, "UC-T03-03 warp handles finite extreme displacement, disabled bypass and missing inputs without fabricated terrain.");
                Check(warpError < 1e-6f && rockError < 1e-6f, "UC-T03-03 specialized warp/strata transform trench reference exactly like independent ground.");
                Matrix rockMask = evaluator.PullNode(strata, 1, tile, stop) as Matrix;
                Check(rockMask.trenchReference == null, "UC-T03-03 rock-type masks do not exempt terrain from guards.");
            }
            finally { tile.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(graph); }
        }

        // UC-T03-03: water shaping remains part of guarded ground; its cut cannot inherit
        // an unrelated river exemption, and water masks/levels never carry that exemption.
        private static void TestWaterModifierTrenchReferences()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 33, 33), Vector3.zero, new Vector3(320f, 600f, 320f), 600f);
            try
            {
                var carved = new ContractHeightFeed { id = "water-contract-carved", trench = true };
                var ground = new ContractHeightFeed { id = "water-contract-ground" };
                var mask = new ContractHeightFeed { id = "water-contract-mask", warp = true, constant = 0.8f };
                var level = new ContractHeightFeed { id = "water-contract-level", warp = true, constant = 0.43f };
                graph.nodes.AddRange(new Generator[] { carved, ground, mask, level });
                Generator[] shaped = {
                    new LakeBasinNode { id = "contract-lake", maxDepth = 0.04f },
                    new WaterBankSeatNode { id = "contract-seat", strength = 0.75f },
                    new SwampBasinNode { id = "contract-swamp", swampLevel = 0.42f, flatten = 0.7f }
                };
                Generator[] references = {
                    new LakeBasinNode { id = "contract-lake-base", maxDepth = 0.04f },
                    new WaterBankSeatNode { id = "contract-seat-base", strength = 0.75f },
                    new SwampBasinNode { id = "contract-swamp-base", swampLevel = 0.42f, flatten = 0.7f }
                };
                for (int pair = 0; pair < shaped.Length; pair++)
                    for (int side = 0; side < 2; side++)
                    {
                        Generator node = side == 0 ? shaped[pair] : references[pair];
                        graph.nodes.Add(node);
                        graph.AddConnection(new Connection(side == 0 ? carved.id : ground.id, 0, node.id, 0));
                        graph.AddConnection(new Connection(mask.id, 0, node.id, pair == 1 ? 2 : 1));
                        if (pair != 2) graph.AddConnection(new Connection(level.id, 0, node.id, pair == 1 ? 1 : 2));
                    }
                var ocean = new OceanLevelNode { id = "contract-ocean" };
                var lakeFill = new LakeFillNode { id = "contract-lake-fill" };
                graph.nodes.AddRange(new Generator[] { ocean, lakeFill });
                graph.AddConnection(new Connection(carved.id, 0, ocean.id, 0));
                graph.AddConnection(new Connection(carved.id, 0, lakeFill.id, 0));
                graph.compiledPlan = CompiledGraphPlan.Build(graph);
                var evaluator = new GraphEvaluator(graph); tile.Bind(evaluator); var stop = new StopToken();
                for (int pair = 0; pair < shaped.Length; pair++)
                {
                    Matrix actual = evaluator.PullNode(shaped[pair], 0, tile, stop) as Matrix;
                    Matrix reference = evaluator.PullNode(references[pair], 0, tile, stop) as Matrix;
                    bool matches = actual != null && reference != null && actual.trenchReference != null;
                    if (matches) for (int i = 0; i < actual.arr.Length; i++)
                        matches &= Mathf.Abs(actual.trenchReference.arr[i] - reference.arr[i]) < 1e-6f;
                    Check(matches, "UC-T03-03 " + shaped[pair].NodeName + " shapes reference ground exactly like an independent uncarved branch.");
                }
                Matrix oceanMask = evaluator.PullNode(ocean, 0, tile, stop) as Matrix;
                Matrix waterLevel = evaluator.PullNode(lakeFill, 2, tile, stop) as Matrix;
                Matrix swampMask = evaluator.PullNode(shaped[2], 1, tile, stop) as Matrix;
                Check(oceanMask.trenchReference == null && waterLevel.trenchReference == null && swampMask.trenchReference == null,
                    "UC-T03-03 ocean/swamp masks and lake water levels carry no terrain trench reference.");
            }
            finally { tile.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(graph); }
        }

        // UC-T03-03: validate the existing control fallbacks and geometric calculations through real graph pulls.
        private static void TestManualCourseInvalidControls()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 33, 33), Vector3.zero, new Vector3(320f, 600f, 320f), 600f);
            try
            {
                var ground = new ContractHeightFeed { id = "invalid-course-ground", warp = true, constant = 0.6f };
                var course = new RiverCourseInputNode.ManualRiverCourse {
                    pointsXZ = new[] { new Vector2(20f, 160f), new Vector2(300f, 160f) },
                    halfWidthM = new[] { float.NaN, float.PositiveInfinity },
                    bedY = new[] { 294f }, depthM = new[] { 6f }
                };
                var invalid = new RiverCourseInputNode { id = "invalid-course-controls", fallbackHalfWidthMetres = float.NaN,
                    edgeFeatherMetres = float.PositiveInfinity, bankReachMetres = float.NaN, bankSlope = float.NegativeInfinity };
                invalid.SetManualCourses(new System.Collections.Generic.List<RiverCourseInputNode.ManualRiverCourse> { course });
                var valid = new RiverCourseInputNode { id = "invalid-course-defaults", fallbackHalfWidthMetres = 3f,
                    edgeFeatherMetres = 0.01f, bankReachMetres = 0f, bankSlope = 0.5f };
                invalid.CopyInputsTo(valid);
                var negative = new RiverCourseInputNode { id = "negative-course-controls", fallbackHalfWidthMetres = -10f,
                    edgeFeatherMetres = -10f, bankReachMetres = -10f, bankSlope = -10f };
                invalid.CopyInputsTo(negative);
                var minimum = new RiverCourseInputNode { id = "minimum-course-controls", fallbackHalfWidthMetres = 0.1f,
                    edgeFeatherMetres = 0.01f, bankReachMetres = 0f, bankSlope = 0.05f };
                invalid.CopyInputsTo(minimum);
                var disabled = new RiverCourseInputNode { id = "disabled-course-controls", disabled = true };
                invalid.CopyInputsTo(disabled);
                var missing = new RiverCourseInputNode { id = "missing-course-height" };
                invalid.CopyInputsTo(missing);
                var extreme = new RiverCourseInputNode { id = "extreme-course-data", bankReachMetres = float.MaxValue,
                    edgeFeatherMetres = float.MaxValue, fallbackHalfWidthMetres = float.MaxValue, bankSlope = float.MaxValue };
                extreme.SetManualCourses(new System.Collections.Generic.List<RiverCourseInputNode.ManualRiverCourse> {
                    new RiverCourseInputNode.ManualRiverCourse {
                        pointsXZ = new[] { new Vector2(-float.MaxValue, 160f), new Vector2(float.MaxValue, 160f) },
                        bedY = new[] { -float.MaxValue, float.MaxValue }, depthM = new[] { float.MaxValue }
                    }
                });
                graph.nodes.AddRange(new Generator[] { ground, invalid, valid, negative, minimum, disabled, missing, extreme });
                foreach (Generator node in new Generator[] { invalid, valid, negative, minimum, disabled, extreme })
                    graph.AddConnection(new Connection(ground.id, 0, node.id, 0));
                graph.compiledPlan = CompiledGraphPlan.Build(graph);
                var evaluator = new GraphEvaluator(graph); tile.Bind(evaluator); var stop = new StopToken();
                Matrix source = evaluator.PullNode(ground, 0, tile, stop) as Matrix;
                bool finite = true, defaults = true, limits = true, missingIsEmpty = true;
                for (int port = 0; port < 2; port++)
                {
                    Matrix actual = evaluator.PullNode(invalid, port, tile, stop) as Matrix;
                    Matrix expected = evaluator.PullNode(valid, port, tile, stop) as Matrix;
                    Matrix belowRange = evaluator.PullNode(negative, port, tile, stop) as Matrix;
                    Matrix atMinimum = evaluator.PullNode(minimum, port, tile, stop) as Matrix;
                    Matrix absent = evaluator.PullNode(missing, port, tile, stop) as Matrix;
                    Matrix large = evaluator.PullNode(extreme, port, tile, stop) as Matrix;
                    // Matrix-typed mask outlets share the evaluator's generic disabled bypass contract.
                    Check(ReferenceEquals(evaluator.PullNode(disabled, port, tile, stop), source),
                        "UC-T03-03 disabled manual-course outlet " + port + " forwards the connected height without executing carving.");
                    for (int i = 0; i < actual.arr.Length; i++)
                    {
                        defaults &= actual.arr[i] == expected.arr[i];
                        limits &= belowRange.arr[i] == atMinimum.arr[i];
                        missingIsEmpty &= absent.arr[i] == 0f;
                        finite &= !float.IsNaN(large.arr[i]) && !float.IsInfinity(large.arr[i]);
                    }
                }
                Check(defaults && limits, "UC-T03-03 manual-course nonfinite and below-range settings match documented defaults/minima.");
                Check(missingIsEmpty && finite, "UC-T03-03 missing height produces empty fields and finite extreme course data produces finite fields.");
                float along;
                float distance = RiverCourseInputNode.DistanceToSegment(0f, 180f, -float.MaxValue, 160f, float.MaxValue, 160f, out along);
                Check(distance == 20f && along == 0.5f,
                    "UC-T03-03 extreme finite segment coordinates preserve the midpoint distance and interpolation parameter.");
                Check(RiverCourseInputNode.MaskAt(0f, 0f, null, 1f) == 0f,
                    "UC-T03-03 absent prepared courses return a dry mask.");
            }
            finally { tile.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(graph); }
        }

        // UC-T03-03: pins the previously ineffective fallback on mask/bounds as well as
        // excavation, including a new snapshot after changing the fallback without repushing geometry.
        private static void TestManualCourseControlContracts()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            var tile = new TileData(Coord.Zero, new CoordRect(0, 0, 33, 33), Vector3.zero, new Vector3(320f, 600f, 320f), 600f);
            try
            {
                var ground = new ContractHeightFeed { id = "course-control-ground", warp = true, constant = 0.6f };
                var course = new RiverCourseInputNode.ManualRiverCourse {
                    pointsXZ = new[] { new Vector2(20f, 160f), new Vector2(300f, 160f) },
                    bedY = new[] { 294f, 294f }, depthM = new[] { 6f, 6f }
                };
                Action<float> bounds = course.ComputeBounds; // Original public method-group signature remains valid.
                bounds(1f);
                var narrow = new RiverCourseInputNode { id = "course-narrow", fallbackHalfWidthMetres = 10f, bankReachMetres = 0f, edgeFeatherMetres = 1f };
                narrow.SetManualCourses(new System.Collections.Generic.List<RiverCourseInputNode.ManualRiverCourse> { course });
                var wide = new RiverCourseInputNode { id = "course-wide-snapshot", fallbackHalfWidthMetres = 40f, bankReachMetres = 0f, edgeFeatherMetres = 1f };
                narrow.CopyInputsTo(wide);
                var feather = new RiverCourseInputNode { id = "course-feather", fallbackHalfWidthMetres = 10f, bankReachMetres = 0f, edgeFeatherMetres = 40f };
                narrow.CopyInputsTo(feather);
                var bank = new RiverCourseInputNode { id = "course-bank", fallbackHalfWidthMetres = 10f, bankReachMetres = 40f, bankSlope = 0.5f };
                narrow.CopyInputsTo(bank);
                var steep = new RiverCourseInputNode { id = "course-steep", fallbackHalfWidthMetres = 10f, bankReachMetres = 40f, bankSlope = 1f };
                narrow.CopyInputsTo(steep);
                var maskOnly = new RiverCourseInputNode { id = "course-mask-only", fallbackHalfWidthMetres = 40f, carveTrench = false };
                narrow.CopyInputsTo(maskOnly);
                var emptyBed = new RiverCourseInputNode { id = "course-empty-bed", fallbackHalfWidthMetres = 40f };
                emptyBed.SetManualCourses(new System.Collections.Generic.List<RiverCourseInputNode.ManualRiverCourse> {
                    new RiverCourseInputNode.ManualRiverCourse { pointsXZ = course.pointsXZ, bedY = Array.Empty<float>(), depthM = Array.Empty<float>() }
                });
                var partialWidths = new RiverCourseInputNode { id = "course-partial-widths", fallbackHalfWidthMetres = 40f, bankReachMetres = 0f };
                partialWidths.SetManualCourses(new System.Collections.Generic.List<RiverCourseInputNode.ManualRiverCourse> {
                    new RiverCourseInputNode.ManualRiverCourse { pointsXZ = course.pointsXZ, bedY = course.bedY, depthM = course.depthM,
                        halfWidthM = new[] { 10f } }
                });
                graph.nodes.Add(ground);
                foreach (Generator node in new Generator[] { narrow, wide, feather, bank, steep, maskOnly, emptyBed, partialWidths })
                {
                    graph.nodes.Add(node);
                    graph.AddConnection(new Connection(ground.id, 0, node.id, 0));
                }
                var terminal = new Nodes.Outputs.HeightOutputNode { id = "course-contract-terminal",
                    enforceSeaLevelFloor = false, enforceCoastalShelf = false, enforceContinentalRise = false };
                graph.nodes.Add(terminal);
                graph.AddConnection(new Connection(wide.id, 0, terminal.id, 0));
                graph.compiledPlan = CompiledGraphPlan.Build(graph);
                var evaluator = new GraphEvaluator(graph); tile.Bind(evaluator); var stop = new StopToken();
                NodeCatalog.ConnectionContract[] consumers = NodeCatalog.CaptureGroundConnections(graph);
                bool connectedTerminal = false, inventedConsumer = false;
                foreach (NodeCatalog.ConnectionContract consumer in consumers)
                {
                    connectedTerminal |= consumer.sourceId == wide.id && consumer.sourceIndex == 0 &&
                        consumer.targetId == terminal.id && consumer.terminalConsumer && !consumer.targetDisabled &&
                        consumer.valueType == PortType.Matrix && consumer.sourceUnits == consumer.targetUnits;
                    inventedConsumer |= consumer.sourceId == wide.id && consumer.sourceIndex == 1;
                }
                Check(connectedTerminal && !inventedConsumer && consumers.Length == graph.connections.Count,
                    "UC-T03-03 connected-consumer records identify the actual Height Output wire and leave the unwired River Mask unclaimed.");
                const int sample = 18 * 33 + 16; // world (160,180), twenty metres from the authored route.
                Matrix narrowHeight = evaluator.PullNode(narrow, 0, tile, stop) as Matrix;
                Matrix narrowMask = evaluator.PullNode(narrow, 1, tile, stop) as Matrix;
                Matrix wideHeight = evaluator.PullNode(wide, 0, tile, stop) as Matrix;
                Matrix wideMask = evaluator.PullNode(wide, 1, tile, stop) as Matrix;
                Check(narrowHeight.arr[sample] == 0.6f && narrowMask.arr[sample] == 0f &&
                    wideHeight.arr[sample] < 0.59f && wideMask.arr[sample] == 1f,
                    "UC-T03-03 fallback width changes captured bounds, native-bound height and river mask together without repushing the course.");
                Matrix featherMask = evaluator.PullNode(feather, 1, tile, stop) as Matrix;
                Check(featherMask.arr[sample] > 0f && featherMask.arr[sample] < 1f,
                    "UC-T03-03 edge feather widens the mask's transition without fabricating channel width.");
                Matrix bankHeight = evaluator.PullNode(bank, 0, tile, stop) as Matrix;
                Matrix steepHeight = evaluator.PullNode(steep, 0, tile, stop) as Matrix;
                Check(bankHeight.arr[sample] < narrowHeight.arr[sample] && steepHeight.arr[sample] > bankHeight.arr[sample],
                    "UC-T03-03 bank reach and rise/run slope independently change the authored bank grade.");
                Matrix passHeight = evaluator.PullNode(maskOnly, 0, tile, stop) as Matrix;
                Matrix emptyHeight = evaluator.PullNode(emptyBed, 0, tile, stop) as Matrix;
                Matrix partialHeight = evaluator.PullNode(partialWidths, 0, tile, stop) as Matrix;
                Matrix partialMask = evaluator.PullNode(partialWidths, 1, tile, stop) as Matrix;
                Check(passHeight.arr[sample] == 0.6f && emptyHeight.arr[sample] == 0.6f,
                    "UC-T03-03 carve-off and empty bed/depth arrays preserve ground without an indexing exception.");
                Check(partialHeight.arr[sample] < 0.59f && partialMask.arr[sample] == 1f,
                    "UC-T03-03 missing endpoint widths use the same fallback interpolation for height and mask.");
                evaluator.Evaluate(tile, stop);
                Check(tile.outputs.heightmap != null && Mathf.Abs(tile.outputs.heightmap[18, 16] - wideHeight.arr[sample]) < 1e-6f,
                    "UC-T03-03 changing manual fallback width reaches the connected production Height Output.");
                bool rejected = false;
                try
                {
                    narrow.SetManualCourses(new System.Collections.Generic.List<RiverCourseInputNode.ManualRiverCourse> {
                        new RiverCourseInputNode.ManualRiverCourse { pointsXZ = new[] { new Vector2(float.NaN, 0f), Vector2.one } }
                    });
                }
                catch (ArgumentException) { rejected = true; }
                Matrix preserved = narrow.Generate(0, tile, stop) as Matrix; // bypass the node cache to inspect its retained preparation.
                Check(rejected && preserved.arr[16 * 33 + 16] < 0.5f,
                    "UC-T03-03 nonfinite course input is rejected before replacing the last valid prepared course.");
            }
            finally { tile.ReleaseProductMemory(); UnityEngine.Object.DestroyImmediate(graph); }
        }
    }
}
