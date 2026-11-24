// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    TextureProducts.cs
//  Purpose: The data that flows down a texture-graph wire.
//
//  WHAT THE TEXTURE GRAPH ACTUALLY PRODUCES
//  ----------------------------------------
//  Not pixels. The terrain is drawn by a fixed uber-shader
//  (UniverseCreator/TerrainLit), so this graph's job is to describe a material:
//  which textures go in which layer, how snow accumulates, how wet the ground is.
//  Its terminal node writes that description onto a Material and a TerrainLayer
//  set, and the GPU does the per-pixel work at 60 Hz instead of the CPU doing it
//  once and baking the result.
//
//  That is the whole reason these products hold parameters rather than colour
//  fields. A node that "blends snow over rock" here does not composite two
//  images - it records a snow line, a slope limit and a coverage, because that is
//  what the shader can act on per pixel, per frame, under any lighting.
//
//  THREADING
//  ---------
//  Unlike the height graph, the texture graph runs on the MAIN thread. It holds
//  Texture2D and Material references, and its terminal node calls into the asset
//  database. There is nothing to gain from a worker here: the work is a few
//  hundred dictionary writes, not a million-pixel erosion pass. Keeping it on the
//  main thread is what makes holding those references legal.
//
//  The one exception is baked maps - the occlusion raymarch and the noise bake -
//  which are real per-pixel CPU work. Those produce a Matrix through the same
//  managed float[] path the height graph uses, and are turned into a Texture2D at
//  the very end, on the main thread, by the Shader Output node.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    // -------------------------------------------------------------------------
    //  Sampling intent
    // -------------------------------------------------------------------------

    public enum TextureFilterKind { Point = 0, Bilinear = 1, Trilinear = 2 }
    public enum TextureWrapKind { Repeat = 0, Clamp = 1, Mirror = 2 }

    /// <summary>
    /// A texture plus how it should be read. The sampling intent travels with the
    /// reference because a texture's correct filter is a property of what it means
    /// - a gradient LUT must clamp or its ends wrap into each other - and not of
    /// the slot it happens to land in.
    /// </summary>
    [System.Serializable]
    public class TextureRef
    {
        public Texture2D texture;
        public TextureFilterKind filter = TextureFilterKind.Bilinear;
        public TextureWrapKind wrap = TextureWrapKind.Repeat;
        public float mipBias;
        internal bool isGenerated; // UC-T02-06: only evaluation-created textures may be persisted/released by the graph's resource owner.
        internal TileData.TextureResourceOwner resourceOwner; // UC-T07-02: copies share native lifetime, never duplicate byte reservations.

        /// <summary>UC-T07-02: optional captured image semantics; null identifies a legacy/imported reference with unspecified pixel encoding.</summary>
        public TextureImageDescriptor? ImageDescription { get; internal set; }

        public TextureRef() { }

        public TextureRef(Texture2D texture)
        {
            this.texture = texture;
        }

        /// <summary>UC-T01-12: copies sampling intent while borrowing the same native texture.</summary>
        /// <returns>Independent descriptor, including an absent texture; no native allocation or ownership transfer occurs.</returns>
        /// <remarks>Descriptor edits are local. Native texture mutation/destruction remains with its asset or bake owner.</remarks>
        public TextureRef Copy()
        {
            return new TextureRef(texture) { filter = filter, wrap = wrap, mipBias = mipBias, isGenerated = isGenerated,
                ImageDescription = ImageDescription, resourceOwner = resourceOwner }; // UC-T07-02: consumers copy immutable semantics while borrowing the same native owner.
        }

        /// <summary>Retains a generated native texture beyond its evaluation tile's lifetime.</summary>
        /// <returns>Main-thread disposable consumer pin, or null for an imported texture whose asset owner controls its lifetime.</returns>
        /// <exception cref="System.InvalidOperationException">Called off the main thread or after a generated texture retired.</exception>
        /// <remarks>Acquire before releasing the tile; dispose after the final renderer/preview use. Copy alone does not acquire a pin.</remarks>
        public System.IDisposable AcquireConsumer()
        {
            GraphExecutionGuard.RequireMainThread("Texture consumer acquisition");
            return resourceOwner?.Acquire();
        }

        /// <summary>
        /// Applies the intent to the texture object. Only touches a setting that
        /// actually differs: writing filterMode on an imported asset marks it
        /// dirty, and dirtying every texture a graph references on every
        /// evaluation would turn a slider drag into a re-import storm.
        /// </summary>
        public void Apply()
        {
            GraphExecutionGuard.RequireMainThread("Texture sampling intent"); // UC-T07-02: native metadata checks and mutation have one supported thread owner.
            if (texture == null) return;
            if (ImageDescription.HasValue)
            {
                TextureImageDescriptor declared = ImageDescription.Value; declared.Validate();
                if (texture.width != declared.Width || texture.height != declared.Height ||
                    (declared.Mips == TextureImageMipPolicy.Generate && texture.mipmapCount <= 1 && declared.PixelCount > 1))
                    throw new System.ArgumentException("UC-T07-02: native texture no longer matches its captured image contract.");
            }

            FilterMode fm = filter == TextureFilterKind.Point ? FilterMode.Point
                          : filter == TextureFilterKind.Trilinear ? FilterMode.Trilinear
                          : FilterMode.Bilinear;
            if (texture.filterMode != fm) texture.filterMode = fm;

            TextureWrapMode wm = wrap == TextureWrapKind.Clamp ? TextureWrapMode.Clamp
                               : wrap == TextureWrapKind.Mirror ? TextureWrapMode.Mirror
                               : TextureWrapMode.Repeat;
            if (texture.wrapMode != wm) texture.wrapMode = wm;

            if (!Mathf.Approximately(texture.mipMapBias, mipBias))
                texture.mipMapBias = mipBias;
        }
    }

    // -------------------------------------------------------------------------
    //  UV transform
    // -------------------------------------------------------------------------

    /// <summary>
    /// Tiling, offset and rotation for one texture. Tiling is expressed as the
    /// world size of one tile in METRES, not as a repeat count, because a repeat
    /// count means something different on every terrain size - change the world
    /// from 1 km to 4 km and every material silently quadruples in scale.
    /// </summary>
    public class UVTransform
    {
        public Vector2 tileSizeMetres = new Vector2(8f, 8f);
        public Vector2 offset = Vector2.zero;
        public float rotationDegrees;

        public UVTransform() { }

        public UVTransform(Vector2 tileSizeMetres)
        {
            this.tileSizeMetres = tileSizeMetres;
        }

        public UVTransform Copy()
        {
            return new UVTransform
            {
                tileSizeMetres = tileSizeMetres,
                offset = offset,
                rotationDegrees = rotationDegrees
            };
        }

        /// <summary>
        /// Guards the tile size before it reaches Unity. A TerrainLayer with a
        /// zero tileSize divides by zero inside the terrain renderer and paints
        /// the whole layer as one stretched texel, which reads as a solid colour
        /// and sends people looking at their textures instead of at this number.
        /// </summary>
        public Vector2 SafeTileSize
        {
            get
            {
                return new Vector2(
                    Mathf.Max(Mathf.Abs(tileSizeMetres.x), 0.01f),
                    Mathf.Max(Mathf.Abs(tileSizeMetres.y), 0.01f));
            }
        }
    }
}
