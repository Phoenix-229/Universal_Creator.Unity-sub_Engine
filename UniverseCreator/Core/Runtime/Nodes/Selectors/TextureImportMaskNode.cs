// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    TextureImportMaskNode.cs
//  Purpose: Reads a Texture2D as a mask, sampled at the tile's world position
//           and warped/wrapped as needed. The input height is ignored. Used for
//           hand-painted masks, scanned art, or imported GIS data that marks
//           biomes or zones.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Selectors
{
    [System.Serializable]
    public class TextureImportMaskNode : SelectorNode
    {
        public override string NodeName { get { return "Texture Import Mask"; } }
        public override NodeCategory Category { get { return NodeCategory.Selector; } }
        public override string Tooltip { get { return "Import a Texture2D as a mask, sampled at world position."; } }

        public Texture2D maskTexture;
        public float worldScale = 1000f;
        public float intensity = 1f;
        public float offset;
        public Vector2 worldOffset;

        private float[] _pixels;
        private int _width;
        private int _height;

        public override void PrepareMainThread()
        {
            if (maskTexture == null)
            {
                _pixels = null;
                _width = 0;
                _height = 0;
                return;
            }

            _width = maskTexture.width;
            _height = maskTexture.height;

            Color[] colors = maskTexture.GetPixels();
            _pixels = new float[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                _pixels[i] = colors[i].grayscale;
            }
        }

        protected override void Select(Matrix src, Matrix mask, TileData tile, StopToken stop)
        {
            if (_pixels == null || _width == 0 || _height == 0)
            {
                mask.Fill(0f);
                return;
            }

            float safeScale = Mathf.Max(0.0001f, worldScale);
            float invScale = 1f / safeScale;

            Vector2 pixelSize = mask.PixelSize;
            float originX = mask.worldPos.x + worldOffset.x;
            float originZ = mask.worldPos.z + worldOffset.y;

            for (int z = 0; z < mask.rect.size.z; z++)
            {
                if (stop != null && stop.StopRequested) return;

                float wz = originZ + z * pixelSize.y;
                int rowStart = z * mask.rect.size.x;

                for (int x = 0; x < mask.rect.size.x; x++)
                {
                    float wx = originX + x * pixelSize.x;

                    float u = (wx * invScale) % 1f;
                    float v = (wz * invScale) % 1f;
                    if (u < 0f) u += 1f;
                    if (v < 0f) v += 1f;

                    // Bilinear sample.
                    float fx = u * _width;
                    float fy = v * _height;
                    int ix = Mathf.FloorToInt(fx);
                    int iy = Mathf.FloorToInt(fy);
                    float tx = fx - ix;
                    float ty = fy - iy;

                    int ix1 = (ix + 1) % _width;
                    int iy1 = (iy + 1) % _height;

                    float a = _pixels[iy * _width + ix];
                    float b = _pixels[iy * _width + ix1];
                    float c = _pixels[iy1 * _width + ix];
                    float d = _pixels[iy1 * _width + ix1];

                    float top = a + (b - a) * tx;
                    float bot = c + (d - c) * tx;
                    float val = top + (bot - top) * ty;

                    mask.arr[rowStart + x] = InterpolationMath.Clamp01(val * intensity + offset);
                }
            }
        }
    }
}
