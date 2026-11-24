// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    ImportHeightmapNode.cs
//  Purpose: Reads a heightmap from a Texture2D asset and samples it at the
//           tile's world position. The texture wraps seamlessly, so a single
//           import can tile across an infinite plane.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    /// <summary>How world coordinates outside the first texture repeat are handled.</summary>
    public enum HeightmapWrapMode
    {
        /// <summary>Repeat the image edge outward. One island, flat surround.</summary>
        Clamp = 0,
        /// <summary>Repeat the image. Seams show unless the source is tileable.</summary>
        Tile = 1,
        /// <summary>Mirror every other repeat, so opposite edges always meet.</summary>
        PingPong = 2
    }

    [System.Serializable]
    public class ImportHeightmapNode : CreatorNode, IPreparedGraphInputs, IPreparedGraphInputHash
    {
        public override string NodeName { get { return "Import Heightmap"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Sample a Texture2D heightmap at the tile's world position."; } }

        public Texture2D heightmap;
        public float worldScale = 1000f;     // metres per texture repeat
        public float heightScale = 1f;
        public float offset;
        public Vector2 worldOffset;

        [Tooltip("How coordinates beyond one texture repeat are handled.")]
        public HeightmapWrapMode wrapMode = HeightmapWrapMode.Tile;

        private float[] _pixels;
        private int _width;
        private int _height;
        private PreparedRasterInputs _preparedHashInputs;

        /// <summary>UC-T01-07: exposes the captured texel dimensions and grayscale pixels for dependency hashing.</summary>
        /// <returns>Plain prepared raster data; an absent texture has zero dimensions and null pixels.</returns>
        /// <remarks>Main thread, after PrepareMainThread; the worker consumes the same captured array.</remarks>
        public object GetPreparedInputsForHash()
        {
            if (_preparedHashInputs == null) _preparedHashInputs = new PreparedRasterInputs();
            _preparedHashInputs.width = _width;
            _preparedHashInputs.height = _height;
            _preparedHashInputs.pixels = _pixels;
            return _preparedHashInputs;
        }

        /// <summary>UC-T01-07: fingerprints captured raster dimensions and every ordered sample without JSON allocation.</summary>
        /// <returns>Stable 64-bit hash, including absence and canonical zero/NaN samples.</returns>
        /// <remarks>Main thread after PrepareMainThread; does not read the texture or change prepared data.</remarks>
        public ulong GetPreparedInputHash() => PreparedRasterInputs.Hash(_width, _height, _pixels);

        public override void PrepareMainThread()
        {
            if (heightmap == null)
            {
                _pixels = null;
                _width = 0;
                _height = 0;
                return;
            }

            // Snapshot the texture pixels on the main thread so Generate() can
            // run off-thread without touching the Unity API.
            _width = heightmap.width;
            _height = heightmap.height;

            Color[] colors = heightmap.GetPixels();
            _pixels = new float[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                _pixels[i] = colors[i].grayscale;
            }
        }

        protected override void Create(Matrix m, TileData tile, StopToken stop)
        {
            if (_pixels == null || _width == 0 || _height == 0)
            {
                m.Fill(0f);
                return;
            }

            float safeScale = Mathf.Max(0.0001f, worldScale);
            float invScale = 1f / safeScale;

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x + worldOffset.x;
            float originZ = m.worldPos.z + worldOffset.y;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;

                    // Map the world coordinate into texture space. Each mode folds
                    // the coordinate differently, so this runs before the sample
                    // rather than being fixed up afterwards.
                    float u = WrapCoord(wx * invScale);
                    float v = WrapCoord(wz * invScale);

                    // Bilinear sample.
                    float fx = u * _width;
                    float fy = v * _height;
                    int ix = Mathf.FloorToInt(fx);
                    int iy = Mathf.FloorToInt(fy);
                    float tx = fx - ix;
                    float ty = fy - iy;

                    // u and v can land exactly on 1 under Clamp and PingPong, which
                    // would index one past the last texel.
                    if (ix >= _width) { ix = _width - 1; tx = 1f; }
                    if (iy >= _height) { iy = _height - 1; ty = 1f; }

                    // The neighbour tap wraps only when the mode actually tiles;
                    // under Clamp and PingPong the edge texel repeats instead, or
                    // the last column would blend back into the first one.
                    int ix1, iy1;
                    if (wrapMode == HeightmapWrapMode.Tile)
                    {
                        ix1 = (ix + 1) % _width;
                        iy1 = (iy + 1) % _height;
                    }
                    else
                    {
                        ix1 = Mathf.Min(ix + 1, _width - 1);
                        iy1 = Mathf.Min(iy + 1, _height - 1);
                    }

                    float a = _pixels[iy * _width + ix];
                    float b = _pixels[iy * _width + ix1];
                    float c = _pixels[iy1 * _width + ix];
                    float d = _pixels[iy1 * _width + ix1];

                    float top = a + (b - a) * tx;
                    float bot = c + (d - c) * tx;
                    float val = top + (bot - top) * ty;

                    m.arr[rowStart + x] = val * heightScale + offset;
                }
            }
        }

        /// <summary>Folds a repeat-space coordinate into [0,1] per the wrap mode.</summary>
        private float WrapCoord(float t)
        {
            switch (wrapMode)
            {
                case HeightmapWrapMode.Clamp:
                    return InterpolationMath.Clamp01(t);

                case HeightmapWrapMode.PingPong:
                {
                    // Fold over a period of 2, then mirror the upper half. Repeat.PingPong
                    // is a Unity API call and this runs off the main thread.
                    float p = t - 2f * Mathf.Floor(t * 0.5f);
                    return p > 1f ? 2f - p : p;
                }

                default:
                {
                    float f = t - Mathf.Floor(t);
                    return f < 0f ? f + 1f : f;
                }
            }
        }
    }
}
