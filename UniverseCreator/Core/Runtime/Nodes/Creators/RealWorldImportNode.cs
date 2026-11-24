// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    RealWorldImportNode.cs
//  Purpose: Imports geo-referenced elevation data from a supplied DEM tile or
//           height texture, aligned to a real-world bounding box (latitude/longitude
//           or projected metres). This is where you plug SRTM, ASTER, or lidar
//           tiles into the graph.
//
//  The user supplies the texture, the bounding box in world coordinates (in
//  whatever projection the project uses — could be lat/lon degrees, UTM metres,
//  or a game-space coordinate system), and height scale. The node samples the
//  texture at the intersection of the tile's world bounds and the data bounds.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Creators
{
    [System.Serializable]
    public class RealWorldImportNode : CreatorNode, IPreparedGraphInputs, IPreparedGraphInputHash
    {
        public override string NodeName { get { return "Real-World Import"; } }
        public override NodeCategory Category { get { return NodeCategory.Creator; } }
        public override string Tooltip { get { return "Geo-referenced DEM import with explicit world bounds."; } }

        public Texture2D demTexture;

        /// <summary>Min world coordinate (X, Z) of the DEM tile.</summary>
        public Vector2 boundsMin = new Vector2(-1000f, -1000f);

        /// <summary>Max world coordinate (X, Z) of the DEM tile.</summary>
        public Vector2 boundsMax = new Vector2(1000f, 1000f);

        public float heightScale = 1f;
        public float heightOffset;

        private float[] _pixels;
        private int _width;
        private int _height;
        private PreparedRasterInputs _preparedHashInputs;

        /// <summary>UC-T01-07: exposes the captured DEM texel dimensions and grayscale pixels for dependency hashing.</summary>
        /// <returns>Plain prepared raster data; an absent DEM has zero dimensions and null pixels.</returns>
        /// <remarks>Main thread, after PrepareMainThread; the worker consumes the same captured array.</remarks>
        public object GetPreparedInputsForHash()
        {
            if (_preparedHashInputs == null) _preparedHashInputs = new PreparedRasterInputs();
            _preparedHashInputs.width = _width;
            _preparedHashInputs.height = _height;
            _preparedHashInputs.pixels = _pixels;
            return _preparedHashInputs;
        }

        /// <summary>UC-T01-07: fingerprints captured DEM dimensions and every ordered sample without JSON allocation.</summary>
        /// <returns>Stable 64-bit hash, including absence and canonical zero/NaN samples.</returns>
        /// <remarks>Main thread after PrepareMainThread; does not read the DEM or change prepared data.</remarks>
        public ulong GetPreparedInputHash() => PreparedRasterInputs.Hash(_width, _height, _pixels);

        public override void PrepareMainThread()
        {
            if (demTexture == null)
            {
                _pixels = null;
                _width = 0;
                _height = 0;
                return;
            }

            _width = demTexture.width;
            _height = demTexture.height;

            Color[] colors = demTexture.GetPixels();
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

            Vector2 dataSize = boundsMax - boundsMin;
            if (dataSize.x <= 0f || dataSize.y <= 0f)
            {
                m.Fill(0f);
                return;
            }

            int resX = m.rect.size.x;
            int resZ = m.rect.size.z;
            float mppX = m.PixelSize.x;
            float mppZ = m.PixelSize.y;
            float originX = m.worldPos.x;
            float originZ = m.worldPos.z;

            for (int z = 0; z < resZ; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * mppZ;
                int rowStart = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    float wx = originX + x * mppX;

                    // Map world coord to [0..1] within the DEM bounds.
                    float u = (wx - boundsMin.x) / dataSize.x;
                    float v = (wz - boundsMin.y) / dataSize.y;

                    // Clamp to the DEM extent — pixels outside the data bounds read
                    // as the nearest edge or zero.
                    if (u < 0f || u > 1f || v < 0f || v > 1f)
                    {
                        m.arr[rowStart + x] = 0f;
                        continue;
                    }

                    // Bilinear sample.
                    float fx = u * (_width - 1);
                    float fy = v * (_height - 1);
                    int ix = Mathf.FloorToInt(fx);
                    int iy = Mathf.FloorToInt(fy);
                    float tx = fx - ix;
                    float ty = fy - iy;

                    int ix1 = Mathf.Min(ix + 1, _width - 1);
                    int iy1 = Mathf.Min(iy + 1, _height - 1);

                    float a = _pixels[iy * _width + ix];
                    float b = _pixels[iy * _width + ix1];
                    float c = _pixels[iy1 * _width + ix];
                    float d = _pixels[iy1 * _width + ix1];

                    float top = a + (b - a) * tx;
                    float bot = c + (d - c) * tx;
                    float val = top + (bot - top) * ty;

                    m.arr[rowStart + x] = val * heightScale + heightOffset;
                }
            }
        }
    }
}
