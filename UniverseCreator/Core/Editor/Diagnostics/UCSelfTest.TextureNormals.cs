// UC-T07-05: physical ramp derivatives, border halos and independent normal blend identities.
using System;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestTextureNormals()
        {
            var memory = new ProductMemoryBudget(8 * 1024 * 1024); var tile = MemoryTestTile(memory);
            try
            {
                for (int resolution = 3; resolution <= 5; resolution += 2)
                {
                    float spacing = 4f / (resolution - 1);
                    var d = new TextureImageDescriptor(resolution, resolution, TextureImageColourSpace.Linear, TextureImageSemantic.HeightMetres);
                    var ramp = tile.GenerateTextureImage(d, i => new Color(2 * (i % resolution) * spacing + 3 * (i / resolution) * spacing, 0, 0, 1));
                    var node = new HeightToNormalNode { texelSpacingMetres = new Vector2(spacing, spacing) };
                    var normal = node.Convert(ramp, null, tile, null); Vector3 expected = new Vector3(-2, -3, 1).normalized;
                    bool correct = true;
                    for (int i = 0; i < d.PixelCount; i++) correct &= Vector3.Distance(TextureNormalBlendNode.Decode(normal[i]), expected) < 0.00001f;
                    Check(correct, "UC-T07-05 metre ramp derivatives preserve direction and one-sided borders at resolution " + resolution + ".");
                }
                var heightD = new TextureImageDescriptor(3, 3, TextureImageColourSpace.Linear, TextureImageSemantic.HeightMetres);
                var haloD = new TextureImageDescriptor(5, 5, TextureImageColourSpace.Linear, TextureImageSemantic.HeightMetres);
                var convert = new HeightToNormalNode(); TextureImageProduct left = null;
                for (int cell = 0; cell < 2; cell++)
                {
                    int origin = cell * 2;
                    var core = tile.GenerateTextureImage(heightD, i => new Color((origin + i % 3) * (origin + i % 3), 0, 0, 1));
                    var halo = tile.GenerateTextureImage(haloD, i => new Color((origin + i % 5 - 1) * (origin + i % 5 - 1), 0, 0, 1));
                    var result = convert.Convert(core, halo, tile, null);
                    if (cell == 0) left = result;
                    else Check(Vector3.Distance(TextureNormalBlendNode.Decode(left[5]), TextureNormalBlendNode.Decode(result[3])) < 0.00001f,
                        "UC-T07-05 curved height's shared cell border has identical normals with the true one-texel halo.");
                }
                var flatHeight = tile.GenerateTextureImage(heightD, _ => new Color(12, 0, 0, 1));
                var flat = convert.Convert(flatHeight, null, tile, null);
                Check(flat[0] == new Color(0.5f, 0.5f, 1, 1), "UC-T07-05 constant metre height produces +Z tangent normal with shader-compatible alpha.");
                var normalD = flat.Descriptor;
                var tilted = tile.GenerateTextureImage(normalD, _ => TextureNormalBlendNode.Encode(new Vector3(0.3f, 0.2f, 1).normalized));
                var blend = new TextureNormalBlendNode { mode = TextureNormalBlend.Detail, amount = 1 };
                Check(Vector3.Distance(TextureNormalBlendNode.Decode(blend.Blend(tilted, flat, tile, null)[0]), TextureNormalBlendNode.Decode(tilted[0])) < 0.00001f &&
                    Vector3.Distance(TextureNormalBlendNode.Decode(blend.Blend(flat, tilted, tile, null)[0]), TextureNormalBlendNode.Decode(tilted[0])) < 0.00001f,
                    "UC-T07-05 detail rotation preserves either normal when the other is flat.");
                blend.mode = TextureNormalBlend.Coverage; blend.amount = 0;
                Check(Vector3.Distance(TextureNormalBlendNode.Decode(blend.Blend(tilted, flat, tile, null)[0]), TextureNormalBlendNode.Decode(tilted[0])) < 0.00001f,
                    "UC-T07-05 zero normal coverage preserves the base direction.");
                bool rejected = false; convert.texelSpacingMetres = Vector2.zero;
                try { convert.Convert(flatHeight, null, tile, null); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "UC-T07-05 invalid zero metre spacing is diagnosed.");
                var stop = new StopToken(); stop.RequestStop(); long bytes = memory.ManagedBytes;
                Check(convert.Convert(flatHeight, null, tile, stop) == null && memory.ManagedBytes == bytes, "UC-T07-05 cancelled derivatives allocate no image.");
            }
            finally { tile.ReleaseProductMemory(); Check(memory.ManagedBytes == 0, "UC-T07-05 normal fixtures retire all admitted pixel arrays."); }
        }
    }
}
