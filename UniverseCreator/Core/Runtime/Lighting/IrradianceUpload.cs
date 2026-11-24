// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    IrradianceUpload.cs
//  Purpose: Pack the probe grid into the two textures the shader samples, and build them.
//           An ambient cube has six faces; they ride two textures of three faces each -
//           the positive axes in one, the negative in the other - stacked along the depth
//           axis so a volume of N columns becomes a volume 3N deep. The 3-D and the 2-D
//           atlas paths pack the exact same flat array in the exact same order, so the two
//           differ only in the texture object's shape, never in a texel: that is what lets
//           the atlas fallback, taken where Texture3D is unavailable, render identically to
//           the volume it stands in for. Packing is pure; the two builders wrap it in the
//           texture API and are the only part that must run on the main thread.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Packs an <see cref="IrradianceGrid"/> into the <c>_IrradianceA</c>/<c>_IrradianceB</c>
    /// data the shader reads, and builds either two <see cref="Texture3D"/>s or, behind the
    /// <c>UC_IRRADIANCE_ATLAS</c> keyword, two 2-D atlases carrying the identical bytes. The
    /// pack/unpack pair is the contract the self-test pins so the two paths cannot drift.
    /// </summary>
    public static class IrradianceUpload
    {
        public const string KeywordAtlas = "UC_IRRADIANCE_ATLAS";
        public const string PropIrradianceA = "_IrradianceA";
        public const string PropIrradianceB = "_IrradianceB";
        public const string PropIrradianceDims = "_IrradianceDims";   // (dimX, dimY, dimZ) for sampling

        // Three faces per texture, stacked along the depth axis.
        private const int FacesPerTexture = 3;

        /// <summary>The length of each packed array: one texel per probe per stacked face.</summary>
        public static int PackedLength(IrradianceGrid grid)
        {
            if (grid == null) return 0;
            return grid.dimX * grid.dimY * grid.dimZ * FacesPerTexture;
        }

        /// <summary>The depth of the volume texture: the grid depth times the three faces.</summary>
        public static int VolumeDepth(IrradianceGrid grid)
        {
            return grid == null ? 0 : grid.dimZ * FacesPerTexture;
        }

        // A texel is RGBAHalf: four 16-bit channels, eight bytes. The ambient cube rides
        // two such textures - positive faces in one, negative in the other - so a probe's
        // upload cost is both.
        public const int BytesPerTexel = 8;   // RGBAHalf, four half channels
        private const int TextureCount = 2;    // _IrradianceA and _IrradianceB

        /// <summary>
        /// GPU bytes a grid of this many probes uploads to: two RGBAHalf textures, three
        /// stacked faces per probe in each, so forty-eight bytes a probe. A pure function
        /// of the count and the packing constants above - the same count always sizes to
        /// the same figure - so the inspector can show a bake's cost before a probe is
        /// filled. Returns zero for a non-positive count.
        /// </summary>
        public static long GpuBytes(long probeCount)
        {
            if (probeCount <= 0L) return 0L;
            return probeCount * FacesPerTexture * TextureCount * BytesPerTexel;
        }

        // Flat index of one probe's given stacked face, shared by both paths so the volume
        // and the atlas are literally the same array laid out the same way.
        private static int Flat(int dimX, int dimY, int dimZ, int x, int y, int z, int face)
        {
            int zf = face * dimZ + z;
            return zf * (dimX * dimY) + y * dimX + x;
        }

        /// <summary>
        /// Pack the grid into two arrays: <paramref name="a"/> gets the three positive-axis
        /// faces, <paramref name="b"/> the three negative. Both must be <see cref="PackedLength"/>
        /// long. Silent no-op if a size is wrong, so a mis-sized buffer cannot corrupt memory.
        /// </summary>
        public static void Pack(IrradianceGrid grid, Color[] a, Color[] b)
        {
            if (grid == null || a == null || b == null) return;
            int len = PackedLength(grid);
            if (a.Length < len || b.Length < len) return;
            int dimX = grid.dimX, dimY = grid.dimY, dimZ = grid.dimZ;

            for (int z = 0; z < dimZ; z++)
            for (int y = 0; y < dimY; y++)
            for (int x = 0; x < dimX; x++)
            {
                IrradianceProbe p = grid.probes[grid.Index(x, y, z)];
                a[Flat(dimX, dimY, dimZ, x, y, z, 0)] = ToColor(p.posX);
                a[Flat(dimX, dimY, dimZ, x, y, z, 1)] = ToColor(p.posY);
                a[Flat(dimX, dimY, dimZ, x, y, z, 2)] = ToColor(p.posZ);
                b[Flat(dimX, dimY, dimZ, x, y, z, 0)] = ToColor(p.negX);
                b[Flat(dimX, dimY, dimZ, x, y, z, 1)] = ToColor(p.negY);
                b[Flat(dimX, dimY, dimZ, x, y, z, 2)] = ToColor(p.negZ);
            }
        }

        /// <summary>
        /// Reassemble one probe from the packed arrays - the CPU mirror of the shader's
        /// sample. Both paths carry the same array, so this recovers the same probe whether
        /// the bytes came from a volume or an atlas: the "render identically" guarantee.
        /// </summary>
        public static IrradianceProbe Unpack(Color[] a, Color[] b, int dimX, int dimY, int dimZ,
                                             int x, int y, int z)
        {
            return new IrradianceProbe
            {
                posX = ToVec(a[Flat(dimX, dimY, dimZ, x, y, z, 0)]),
                posY = ToVec(a[Flat(dimX, dimY, dimZ, x, y, z, 1)]),
                posZ = ToVec(a[Flat(dimX, dimY, dimZ, x, y, z, 2)]),
                negX = ToVec(b[Flat(dimX, dimY, dimZ, x, y, z, 0)]),
                negY = ToVec(b[Flat(dimX, dimY, dimZ, x, y, z, 1)]),
                negZ = ToVec(b[Flat(dimX, dimY, dimZ, x, y, z, 2)]),
            };
        }

        /// <summary>Build one volume texture from a packed array (depth = <see cref="VolumeDepth"/>).</summary>
        public static Texture3D BuildVolume(Color[] data, int dimX, int dimY, int dimZ)
        {
            var tex = new Texture3D(dimX, dimY, dimZ * FacesPerTexture, TextureFormat.RGBAHalf, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels(data);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>
        /// Build one 2-D atlas from the same packed array: width is the grid width, height is
        /// the depth-times-faces stack unrolled by rows. Because the flat order matches the
        /// volume's exactly, the atlas holds identical texels - only its shape differs.
        /// </summary>
        public static Texture2D BuildAtlas(Color[] data, int dimX, int dimY, int dimZ)
        {
            int atlasHeight = dimY * dimZ * FacesPerTexture;
            var tex = new Texture2D(dimX, atlasHeight, TextureFormat.RGBAHalf, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels(data);
            tex.Apply(false, false);
            return tex;
        }

        private static Color ToColor(Vector3 v) { return new Color(v.x, v.y, v.z, 1f); }
        private static Vector3 ToVec(Color c) { return new Vector3(c.r, c.g, c.b); }
    }
}
