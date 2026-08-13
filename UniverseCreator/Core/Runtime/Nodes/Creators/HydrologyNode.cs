using UnityEngine;
using UniverseCreator.Nodes;
using System.Collections.Generic;
using System;

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class HydrologyNode : Generator
    {
        public override string NodeName { get { return "Hydrology Spawner"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Filters native River/Lake masks to respect a target count."; } }

        public int targetRiverCount = 3;
        public int targetLakeCount = 2;
        public int tributaries = 1;
        public int distributaries = 0;

        private static readonly Port[] Ins = {
            new Port("Native River Mask", PortType.Matrix, PortDirection.Input),
            new Port("Native Lake Mask", PortType.Matrix, PortDirection.Input)
        };

        private static readonly Port[] Outs = {
            new Port("River Mask", PortType.Matrix, PortDirection.Output, true),
            new Port("Lake Mask", PortType.Matrix, PortDirection.Output, true)
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return Outs; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix inputMask = GetInputMatrix(tile, outputIndex, stop);
            if (inputMask == null) 
            {
                return tile.CreateMatrix();
            }

            int targetCount = outputIndex == 0 ? targetRiverCount : targetLakeCount;
            
            // Limit lake density across the world to ~25% of chunks as per user request
            if (outputIndex == 1 && tile != null) 
            {
                int seed = (tile.chunkCoord.x * 73856093) ^ (tile.chunkCoord.z * 19349663);
                System.Random rng = new System.Random(seed);
                if (rng.Next(100) >= 25) 
                {
                    return tile.CreateMatrix();
                }
                // Only allow 1 lake per chosen chunk so we don't over-spawn
                targetCount = 1;
            }
            else if (outputIndex == 0 && tile != null && tile.worldSize.x > 1f)
            {
                // Dynamic river scale: ~3 large rivers per 25 chunks globally.
                // Since this runs per-chunk, we just want 1 large river on ~12% of chunks
                int seed = (tile.chunkCoord.x * 39856093) ^ (tile.chunkCoord.z * 49349663);
                System.Random rng = new System.Random(seed);
                if (rng.Next(100) >= 12) 
                {
                    return tile.CreateMatrix();
                }
                targetCount = 1;
            }

            if (targetCount <= 0) return new Matrix(inputMask);

            Matrix dst = new Matrix(inputMask);
            FilterTopComponents(dst, targetCount, outputIndex == 1, stop);

            return dst;
        }

        private void FilterTopComponents(Matrix m, int targetCount, bool discardBorders, StopToken stop)
        {
            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            int count = resX * resZ;

            int[] labels = new int[count];
            int currentLabel = 1;
            Dictionary<int, int> componentSizes = new Dictionary<int, int>();
            Dictionary<int, bool> componentTouchesBorder = new Dictionary<int, bool>();

            // Simple connected components (flood fill)
            for (int i = 0; i < count; i++)
            {
                if (stop != null && (i & 1023) == 0 && stop.StopRequested) return;

                if (m.arr[i] > 0.05f && labels[i] == 0)
                {
                    int size = 0;
                    bool touchesBorder = false;
                    Queue<int> q = new Queue<int>();
                    q.Enqueue(i);
                    labels[i] = currentLabel;

                    while (q.Count > 0)
                    {
                        int curr = q.Dequeue();
                        size++;

                        int cx = curr % resX;
                        int cz = curr / resX;

                        // Check if touching chunk seam
                        if (cx <= 1 || cx >= resX - 2 || cz <= 1 || cz >= resZ - 2)
                        {
                            touchesBorder = true;
                        }

                        // Check 4 neighbors
                        if (cx > 0) CheckNeighbor(curr - 1, currentLabel, m, labels, q);
                        if (cx < resX - 1) CheckNeighbor(curr + 1, currentLabel, m, labels, q);
                        if (cz > 0) CheckNeighbor(curr - resX, currentLabel, m, labels, q);
                        if (cz < resZ - 1) CheckNeighbor(curr + resX, currentLabel, m, labels, q);
                    }

                    componentTouchesBorder[currentLabel] = touchesBorder;
                    componentSizes[currentLabel] = size;
                    currentLabel++;
                }
            }

            // If we have fewer components than target, we don't need to cull
            if (componentSizes.Count <= targetCount) return;

            // Sort components by size descending
            List<int> sortedLabels = new List<int>(componentSizes.Keys);
            sortedLabels.Sort((a, b) => componentSizes[b].CompareTo(componentSizes[a]));

            // Keep top targetCount that don't violate border constraints
            HashSet<int> keepLabels = new HashSet<int>();
            int kept = 0;
            foreach (int label in sortedLabels)
            {
                if (discardBorders && componentTouchesBorder[label]) continue;

                keepLabels.Add(label);
                kept++;
                if (kept >= targetCount) break;
            }

            // Zero out unkept components
            for (int i = 0; i < count; i++)
            {
                if (labels[i] == 0 || !keepLabels.Contains(labels[i]))
                {
                    m.arr[i] = 0f;
                }
            }
        }

        private void CheckNeighbor(int idx, int label, Matrix m, int[] labels, Queue<int> q)
        {
            if (m.arr[idx] > 0.05f && labels[idx] == 0)
            {
                labels[idx] = label;
                q.Enqueue(idx);
            }
        }
    }
}
