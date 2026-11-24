// UC-T05-03: companion graphs sample accepted immutable ground; they never pull the main graph's generator chain.
using System;
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Nodes
{
    /// <summary>Reads accepted ground, normals, water, climate biases and material descriptors without executing another graph.</summary>
    [Serializable]
    public sealed class WorldGraphInputNode : Generator
    {
        /// <summary>Optional explicit TerrainWater asset; null selects the bound world's main graph.</summary>
        [Tooltip("Accepted main Node Graph for the bound world. Empty uses that world's main graph; a foreign or unbuilt graph cannot create another world.")]
        public Graph mainGraph;
        /// <summary>UC-T07-03: category selected by the appended accepted-biome membership output; encoded IDs never become blend weights.</summary>
        [Tooltip("Biome whose accepted source membership is sampled. Requires a Biome Splat output in the main graph.")]
        public Biome.BiomeType selectedBiome = Biome.BiomeType.TemperateForest;
        /// <summary>UC-T07-11: real accepted water category selected by the membership output; unrelated categories have zero membership.</summary>
        [Tooltip("Accepted water category for the water membership output. Categories are decoded before sampling; they are never interpolated as IDs.")]
        public WaterBodyKind selectedWaterKind = WaterBodyKind.River;
        /// <summary>UC-T07-11: signed shore distance is clamped beyond this positive search envelope, in metres, at most 256.</summary>
        [Tooltip("Shore search envelope in horizontal world metres, 0…256. Choose at least the widest authored shore band. Beyond it the output is clamped; it is not an unbounded distance transform.")]
        public float shoreSearchMetres = 32;
        public override string NodeName => "World Graph Input";
        public override NodeCategory Category => NodeCategory.Utility;
        public override string Tooltip => "Reuses accepted world ground and surface coverage; pending ground never triggers terrain generation.";
        private static readonly Port[] Out =
        {
            new Port("Final Ground (normalized height)", PortType.Matrix, PortDirection.Output),
            new Port("Native Surface Coverage (0..1)", PortType.Matrix, PortDirection.Output),
            new Port("Surface Normal X (-1..1)", PortType.Matrix, PortDirection.Output),
            new Port("Surface Normal Y (0..1)", PortType.Matrix, PortDirection.Output),
            new Port("Surface Normal Z (-1..1)", PortType.Matrix, PortDirection.Output),
            new Port("Accepted Water Coverage (0..1)", PortType.Matrix, PortDirection.Output),
            new Port("Accepted Temperature Bias (Celsius)", PortType.Matrix, PortDirection.Output),
            new Port("Accepted Precipitation Bias (mm/hour)", PortType.Matrix, PortDirection.Output),
            new Port("Accepted Wind Bias (m/second)", PortType.Matrix, PortDirection.Output),
            new Port("Accepted Material", PortType.Surface, PortDirection.Output),
            new Port("Accepted Biome Membership (0..1)", PortType.Matrix, PortDirection.Output), // UC-T07-03: append to preserve every saved port index.
            new Port("Ground Elevation (world metres)", PortType.Matrix, PortDirection.Output),
            new Port("Ground Slope (degrees)", PortType.Matrix, PortDirection.Output),
            new Port("Selected Water Membership (0..1)", PortType.Matrix, PortDirection.Output),
            new Port("Water Bed Depth (metres)", PortType.Matrix, PortDirection.Output),
            new Port("Water Current X (m/second)", PortType.Matrix, PortDirection.Output),
            new Port("Water Current Z (m/second)", PortType.Matrix, PortDirection.Output), // UC-T07-11: physical observations from this same accepted revision.
            new Port("Selected Shore Distance (signed metres)", PortType.Matrix, PortDirection.Output)
        };
        public override Port[] GetInputs() => SharedPorts.None;
        public override Port[] GetOutputs() => Out;

        /// <summary>Samples a typed immutable accepted region into the requested tile lattice.</summary>
        /// <param name="outputIndex">0: normalized height; 1: native coverage; 2–4: unit normal XYZ; 5: water coverage; 6–8: climate biases; 9: immutable surface; 10: biome membership; 11: world elevation metres; 12: slope degrees; 13: water-category membership; 14: bed depth metres; 15–16: current XZ m/second; 17: selected-category horizontal shore distance, negative wet, positive dry, clamped to shoreSearchMetres.</param>
        /// <param name="tile">Bound companion tile with owned accepted-ground input and positive world height.</param>
        /// <param name="stop">Optional cooperative cancellation token checked each row.</param>
        /// <returns>Matrix with world geometry or a borrowed immutable surface descriptor; null when cancelled.</returns>
        /// <exception cref="InvalidOperationException">Accepted input is absent, belongs to another world/revision or selects a foreign graph.</exception>
        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (tile == null || tile.worldGraphGround == null || !tile.worldGraphGround.Run.Equals(tile.RunId) ||
                (!ReferenceEquals(mainGraph, null) && !ReferenceEquals(mainGraph, tile.worldGraphGround.SourceGraph)))
                throw new InvalidOperationException("World Graph Input is pending: build accepted ground for the bound world's selected main graph.");
            if (outputIndex < 0 || outputIndex >= Out.Length || !RiverChannelSnapshot.Finite(tile.worldHeight) || tile.worldHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(outputIndex));
            if (outputIndex >= 13 && !Enum.IsDefined(typeof(WaterBodyKind), selectedWaterKind))
                throw new ArgumentException("Select a supported accepted water category.", nameof(selectedWaterKind));
            if (outputIndex == 9) // UC-T05-06: material consumers use the prior committed descriptor; writers already copy at their boundary.
            {
                var surface = tile.worldGraphGround.Surface;
                if (surface == null || !surface.TryGet(tile.RunId, GraphProductUnits.SurfaceDescription, out var value))
                    throw new InvalidOperationException("Pending: Apply a Texture Graph material for this accepted region first.");
                return value;
            }
            Matrix result = tile.CreateMatrix();
            for (int z = 0; z < result.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return null;
                for (int x = 0; x < result.rect.size.x; x++)
                {
                    Vector3 p = result.PixelToWorld(x + result.rect.offset.x, z + result.rect.offset.z);
                    bool covered = tile.worldGraphGround.TrySample(p.x, p.z, out float y);
                    float value = covered ? (y - tile.worldPos.y) / tile.worldHeight : 0;
                    if (outputIndex == 1) value = covered ? 1 : 0;
                    else if (outputIndex >= 2 && outputIndex <= 4)
                    { Vector3 normal = tile.worldGraphGround.Snapshot.GroundNormal(p.x, p.z); value = outputIndex == 2 ? normal.x : outputIndex == 3 ? normal.y : normal.z; }
                    else if (outputIndex == 5) value = tile.worldGraphGround.Water != null ? tile.worldGraphGround.Water.Coverage(p.x, p.z) :
                        throw new InvalidOperationException("Accepted water input was not captured for this companion revision.");
                    else if (outputIndex == 10) // UC-T07-03: accepted labels become independent coverage, never interpolated category IDs.
                    {
                        if (!tile.worldGraphGround.Snapshot.TryBiomeMembership(p.x, p.z, (int)selectedBiome, out value))
                            throw new InvalidOperationException("Pending: accepted ground has no biome classifier field for this position.");
                    }
                    else if (outputIndex == 11 || outputIndex == 12)
                    {
                        if (!covered) throw new InvalidOperationException("Pending: accepted final ground does not cover the requested surface position.");
                        value = outputIndex == 11 ? y : Mathf.Acos(Mathf.Clamp(tile.worldGraphGround.Snapshot.GroundNormal(p.x, p.z).y, -1, 1)) * Mathf.Rad2Deg;
                    }
                    else if (outputIndex >= 13)
                    {
                        // UC-T07-11: dry positions are zero; unavailable captured dependencies fail instead of becoming guessed observations.
                        var water = tile.worldGraphGround.Water ?? throw new InvalidOperationException("Pending: capture accepted water for this companion revision before reading water category, bed depth or current.");
                        bool wet = water.TryStillObservation(p.x, p.z, out float level, out Vector2 flow, out WaterBodyKind kind);
                        if (outputIndex == 17) value = water.SignedShoreDistance(p.x, p.z, shoreSearchMetres, selectedWaterKind);
                        else if (outputIndex == 13) value = wet && kind == selectedWaterKind ? 1 : 0;
                        else if (outputIndex == 14)
                        {
                            if (wet && !covered) throw new InvalidOperationException("Pending: bed depth requires accepted final ground beneath the accepted water.");
                            value = wet ? Mathf.Max(0, level - y) : 0;
                        }
                        else value = wet ? (outputIndex == 15 ? flow.x : flow.y) : 0;
                    }
                    else if (outputIndex >= 6 && outputIndex <= 8) value = tile.worldGraphGround.Environment != null ?
                        tile.worldGraphGround.Environment.Sample(outputIndex, p.x, p.z) :
                        throw new InvalidOperationException("Pending: Run baseline climate or dynamic environment before reading accepted climate biases.");
                    result.arr[z * result.rect.size.x + x] = value;
                }
            }
            return result;
        }
    }

    // One region borrows an immutable accepted snapshot under the evaluating tile's ProductMemory pin.
    // No mutable world registry, native TerrainData or authored generator is read by a worker.
    internal sealed class WorldGraphGround
    {
        internal readonly Graph SourceGraph;
        internal readonly RiverChannelSnapshot Snapshot;
        internal readonly WorldGraphWaterInput Water;
        internal readonly WorldGraphEnvironmentInput Environment;
        internal GraphProductHandle<SurfaceProduct> Surface;
        internal GenerationRunId Run => Snapshot.Run;
        internal WorldGraphGround(Graph sourceGraph, RiverChannelSnapshot snapshot, WorldGraphWaterInput water = null,
            WorldGraphEnvironmentInput environment = null)
        { SourceGraph = sourceGraph; Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot)); Water = water; Environment = environment; }
        internal bool TrySample(float x, float z, out float y) => Snapshot.TryGround(x, z, out y);
    }
}
