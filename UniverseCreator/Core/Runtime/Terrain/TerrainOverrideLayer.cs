// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    TerrainOverrideLayer.cs
//  Purpose: Persists manual sculpting edits so they survive graph regeneration.
//
//  WHY THIS EXISTS
//  ---------------
//  A graph re-run rebuilds the heightfield from scratch. Without this layer, any
//  hand-sculpted detail is destroyed the moment someone nudges a noise slider -
//  which makes manual editing useless in practice, because you can never touch
//  the graph again afterwards.
//
//  WHAT IT STORES
//  --------------
//  Deltas, not absolute heights. Storing absolutes would freeze the edited region
//  against all future graph changes: raise the world height and the sculpted area
//  would stay behind at its old altitude, punching a flat scar through the new
//  terrain. A delta rides along with whatever the graph produces underneath.
//
//  Storage is sparse - a dictionary keyed by world-space sample coordinate, not a
//  dense array per chunk. Two reasons:
//
//    1. Edits are almost always a tiny fraction of the world. A dense array would
//       cost the full heightmap again per chunk, mostly to store zeros.
//    2. Chunks are recycled and the world recenters. A per-chunk-index store would
//       have its edits migrate to the wrong place the first time the grid shifts.
//       World-space keys are stable under both.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Sparse, world-space store of manual height edits, re-applied after every
    /// generation so hand-sculpting survives a graph re-run.
    /// </summary>
    [System.Serializable]
    public class TerrainOverrideLayer : ISerializationCallbackReceiver
    {
        // Sample coordinate in world sample space. Quantised to the sample grid,
        // so it is an exact integer key rather than a float comparison.
        [System.Serializable]
        public struct SampleKey
        {
            public int x;
            public int z;

            public SampleKey(int x, int z) { this.x = x; this.z = z; }

            public override int GetHashCode()
            {
                // Large-prime mix. x and z are small and highly correlated, so a
                // plain XOR collides constantly along diagonals.
                unchecked { return (x * 73856093) ^ (z * 19349663); }
            }

            public override bool Equals(object other)
            {
                if (!(other is SampleKey)) return false;
                SampleKey k = (SampleKey)other;
                return k.x == x && k.z == z;
            }
        }

        // Runtime store. Dictionary is not serializable, so it is flattened in
        // OnBeforeSerialize and rebuilt in OnAfterDeserialize.
        private Dictionary<SampleKey, float> deltas = new Dictionary<SampleKey, float>();

        [SerializeField] private List<SampleKey> serializedKeys = new List<SampleKey>();
        [SerializeField] private List<float> serializedValues = new List<float>();

        // Metres per sample the edits were recorded at. Kept so a later resolution
        // change can rescale rather than discard them.
        [SerializeField] private float recordedMetresPerSample = 0f;

        public int EditCount { get { return deltas.Count; } }
        public bool IsEmpty { get { return deltas.Count == 0; } }

        /// <summary>
        /// Records a height delta at a world position. Deltas accumulate, so
        /// repeated strokes over the same sample build up as the user expects.
        /// </summary>
        public void RecordDelta(float worldX, float worldZ, float deltaNormalised, float metresPerSample)
        {
            if (metresPerSample <= 0f) return;
            if (Mathf.Approximately(deltaNormalised, 0f)) return;

            // First edit fixes the sample scale this layer is expressed in.
            if (recordedMetresPerSample <= 0f) recordedMetresPerSample = metresPerSample;

            SampleKey key = KeyFor(worldX, worldZ, metresPerSample);

            float existing;
            if (deltas.TryGetValue(key, out existing)) deltas[key] = existing + deltaNormalised;
            else deltas[key] = deltaNormalised;

            // Prune samples that cancelled back to flat, so an undo-by-inverse-stroke
            // does not leave the store growing forever.
            if (Mathf.Abs(deltas[key]) < 1e-6f) deltas.Remove(key);
        }

        /// <summary>
        /// Applies every recorded delta that falls inside this chunk to its height
        /// array. Called after the graph has written its own heights, so the edit
        /// rides on top of whatever the graph just produced.
        ///
        /// heights is indexed [z, x] to match Unity's convention.
        /// </summary>
        public void ApplyTo(float[,] heights, Bounds chunkWorldBounds, float metresPerSample)
        {
            if (deltas.Count == 0 || metresPerSample <= 0f || heights == null) return;

            int resZ = heights.GetLength(0);
            int resX = heights.GetLength(1);

            // If world resolution changed since the edits were recorded, the stored
            // keys no longer land on sample centres. Rescaling keeps the edit in the
            // right world position at the cost of some blur, which beats silently
            // dropping the user's work.
            float scale = recordedMetresPerSample > 0f
                        ? recordedMetresPerSample / metresPerSample
                        : 1f;

            int originX = Mathf.RoundToInt(chunkWorldBounds.min.x / metresPerSample);
            int originZ = Mathf.RoundToInt(chunkWorldBounds.min.z / metresPerSample);

            foreach (KeyValuePair<SampleKey, float> pair in deltas)
            {
                int lx = Mathf.RoundToInt(pair.Key.x * scale) - originX;
                int lz = Mathf.RoundToInt(pair.Key.z * scale) - originZ;

                if (lx < 0 || lx >= resX || lz < 0 || lz >= resZ) continue;

                heights[lz, lx] = Mathf.Clamp01(heights[lz, lx] + pair.Value);
            }
        }

        /// <summary>
        /// True if any recorded edit falls inside these bounds. Lets a caller skip
        /// the apply pass for the majority of chunks that were never touched.
        /// </summary>
        public bool AffectsRegion(Bounds worldBounds, float metresPerSample)
        {
            if (deltas.Count == 0 || metresPerSample <= 0f) return false;

            int minX = Mathf.FloorToInt(worldBounds.min.x / metresPerSample);
            int maxX = Mathf.CeilToInt(worldBounds.max.x / metresPerSample);
            int minZ = Mathf.FloorToInt(worldBounds.min.z / metresPerSample);
            int maxZ = Mathf.CeilToInt(worldBounds.max.z / metresPerSample);

            foreach (KeyValuePair<SampleKey, float> pair in deltas)
            {
                if (pair.Key.x >= minX && pair.Key.x <= maxX &&
                    pair.Key.z >= minZ && pair.Key.z <= maxZ) return true;
            }
            return false;
        }

        /// <summary>
        /// Captures the difference between an edited heightfield and the baseline that
        /// was last written to it. This is how a sculpt made with Unity's own terrain
        /// tools gets pulled into the layer - the user never calls RecordDelta by hand.
        /// Differences accumulate onto whatever is already stored, so calling this
        /// repeatedly across many sculpt sessions is correct.
        /// Returns how many samples were recorded.
        /// </summary>
        public int CaptureDifference(float[,] edited, float[,] generated,
                                     Bounds chunkWorldBounds, float metresPerSample,
                                     float threshold = 1e-5f)
        {
            if (edited == null || generated == null || metresPerSample <= 0f) return 0;

            int resZ = Mathf.Min(edited.GetLength(0), generated.GetLength(0));
            int resX = Mathf.Min(edited.GetLength(1), generated.GetLength(1));

            if (recordedMetresPerSample <= 0f) recordedMetresPerSample = metresPerSample;

            int originX = Mathf.RoundToInt(chunkWorldBounds.min.x / metresPerSample);
            int originZ = Mathf.RoundToInt(chunkWorldBounds.min.z / metresPerSample);
            int captured = 0;

            for (int z = 0; z < resZ; z++)
            {
                for (int x = 0; x < resX; x++)
                {
                    float delta = edited[z, x] - generated[z, x];

                    // Threshold rejects float noise from the round-trip through
                    // TerrainData, which would otherwise record the entire chunk.
                    if (Mathf.Abs(delta) < threshold) continue;

                    // Accumulate, don't assign. The baseline passed in is the array
                    // this layer already wrote its deltas into, so the difference is
                    // only what the user added since - assigning it would throw away
                    // every earlier stroke at this sample.
                    SampleKey key = new SampleKey(originX + x, originZ + z);

                    float existing;
                    if (deltas.TryGetValue(key, out existing)) deltas[key] = existing + delta;
                    else deltas[key] = delta;

                    if (Mathf.Abs(deltas[key]) < 1e-6f) deltas.Remove(key);
                    captured++;
                }
            }
            return captured;
        }

        /// <summary>Discards every edit inside a region. Used by the erase tool.</summary>
        public int ClearRegion(Bounds worldBounds, float metresPerSample)
        {
            if (deltas.Count == 0 || metresPerSample <= 0f) return 0;

            int minX = Mathf.FloorToInt(worldBounds.min.x / metresPerSample);
            int maxX = Mathf.CeilToInt(worldBounds.max.x / metresPerSample);
            int minZ = Mathf.FloorToInt(worldBounds.min.z / metresPerSample);
            int maxZ = Mathf.CeilToInt(worldBounds.max.z / metresPerSample);

            List<SampleKey> doomed = new List<SampleKey>();
            foreach (KeyValuePair<SampleKey, float> pair in deltas)
            {
                if (pair.Key.x >= minX && pair.Key.x <= maxX &&
                    pair.Key.z >= minZ && pair.Key.z <= maxZ) doomed.Add(pair.Key);
            }

            for (int i = 0; i < doomed.Count; i++) deltas.Remove(doomed[i]);
            return doomed.Count;
        }

        public void ClearAll()
        {
            deltas.Clear();
            recordedMetresPerSample = 0f;
        }

        private SampleKey KeyFor(float worldX, float worldZ, float metresPerSample)
        {
            return new SampleKey(Mathf.RoundToInt(worldX / metresPerSample),
                                 Mathf.RoundToInt(worldZ / metresPerSample));
        }

        // ------------------------------------------------------------------
        //  Serialization - Unity cannot serialize a Dictionary directly
        // ------------------------------------------------------------------

        public void OnBeforeSerialize()
        {
            serializedKeys.Clear();
            serializedValues.Clear();

            foreach (KeyValuePair<SampleKey, float> pair in deltas)
            {
                serializedKeys.Add(pair.Key);
                serializedValues.Add(pair.Value);
            }
        }

        public void OnAfterDeserialize()
        {
            deltas.Clear();
            int count = Mathf.Min(serializedKeys.Count, serializedValues.Count);
            for (int i = 0; i < count; i++) deltas[serializedKeys[i]] = serializedValues[i];
        }
    }
}
