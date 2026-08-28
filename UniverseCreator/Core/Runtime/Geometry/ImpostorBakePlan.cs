// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ImpostorBakePlan.cs
//  Purpose: The deterministic PLAN behind baking one species' impostor atlas - the
//           part that decides, for a tile grid, exactly which view direction each
//           cell is rendered from, exactly where in the atlas that cell lands, and
//           exactly how the whole grid is spread across editor frames so the bake
//           never blocks. It renders nothing and touches no GPU; it is pure integer
//           and vector arithmetic, which is what lets two runs of the same species
//           produce the SAME sequence of cells - the necessary half of the byte-
//           identical guarantee, the other half being a render that is itself a pure
//           function of the mesh and the direction.
//
//  A BAKE IS A GRID, AND THE GRID IS ORDERED
//  -----------------------------------------
//  An atlas is a viewsPerAxis × viewsPerAxis grid of tilePx tiles. Cell (ix, iz) owns
//  the pixel rect [ix·tile, iz·tile] and is rendered from the direction the octahedral
//  mapping (ImpostorSystem) decodes from that cell's CENTRE UV - hemi-octahedron for a
//  grounded prop never seen from below, full octahedron for something that tumbles. The
//  cells are enumerated in a fixed row-major order with no time, no random and no camera
//  jitter anywhere in the derivation, so the plan a second run builds is identical to the
//  first, cell for cell and rect for rect.
//
//  IT IS AMORTISED, SO THE EDITOR STAYS ALIVE
//  ------------------------------------------
//  Baking fifty species is thousands of tiny renders; doing them in one synchronous loop
//  freezes the editor behind a modal bar. The plan instead hands out at most a fixed
//  budget of tiles per editor frame, so the driver above can render that many, report
//  progress, yield, and resume next frame. The schedule here says which tiles belong to
//  frame k and what fraction of the whole bake is done - the done-when's "leaves the
//  editor responsive" expressed as a cap the driver simply cannot exceed.
//
//  DEVICE-FREE, SO THE PLAN IS PINNED WITHOUT A GPU
//  ------------------------------------------------
//  Everything here is a static function of a few ints and a bool. A self-test reproduces
//  the cell directions, the rects, the every-tile-exactly-once coverage and the per-frame
//  cap with no scene, no camera and no render target. The editor component that owns the
//  RenderTexture, drives the cameras and writes the compressed asset into the project's
//  own folder is the thin layer above this; the arithmetic - the deterministic part - is here.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// One cell of an impostor atlas bake: its grid coordinate, its row-major index, the pixel rectangle it
    /// owns in the atlas, the centre UV of that rectangle, and the unit view direction the octahedral mapping
    /// decodes from that centre - the direction the driver points its bake camera along. A plain value, derived
    /// purely from the grid so it is identical on every run.
    /// </summary>
    public struct ImpostorBakeCell
    {
        public int viewX;             // grid column, 0 .. viewsPerAxis-1
        public int viewZ;             // grid row, 0 .. viewsPerAxis-1
        public int tileIndex;         // row-major index = viewZ * viewsPerAxis + viewX
        public Vector2 uvCenter;      // centre UV of this cell's tile, in [0,1]²
        public Vector3 viewDirection; // unit direction the tile is rendered from
        public RectInt pixelRect;     // the tile's pixel rectangle in the atlas
    }

    /// <summary>
    /// The deterministic plan for baking one species' impostor atlas: the per-cell view directions and pixel
    /// rects, and the frame-amortised schedule that keeps the editor responsive. Pure integer/vector arithmetic
    /// reusing <see cref="ImpostorSystem"/>'s octahedral mapping; a second run builds the identical plan, which
    /// is the deterministic half of the byte-identical-atlas guarantee. Device-free, so a self-test pins it.
    /// </summary>
    public static class ImpostorBakePlan
    {
        /// <summary>Default tiles rendered per editor frame: the cap that keeps a fifty-species bake responsive.</summary>
        public const int DefaultTilesPerFrame = 4;

        /// <summary>Total tiles in a <paramref name="viewsPerAxis"/>-square atlas grid. Fails closed to 0.</summary>
        public static int TileCount(int viewsPerAxis)
        {
            if (viewsPerAxis <= 0) return 0;
            return viewsPerAxis * viewsPerAxis;
        }

        /// <summary>The centre UV of cell (<paramref name="viewX"/>,<paramref name="viewZ"/>) in a
        /// <paramref name="viewsPerAxis"/>-square grid: ((x+0.5)/N, (z+0.5)/N). This is the sample point the
        /// octahedral mapping decodes into the cell's view direction, so every cell samples its tile's middle.</summary>
        public static Vector2 CellUvCenter(int viewX, int viewZ, int viewsPerAxis)
        {
            if (viewsPerAxis <= 0) return new Vector2(0.5f, 0.5f);
            return new Vector2((viewX + 0.5f) / viewsPerAxis, (viewZ + 0.5f) / viewsPerAxis);
        }

        /// <summary>The unit view direction cell (<paramref name="viewX"/>,<paramref name="viewZ"/>) is rendered
        /// from: the octahedral decode of its centre UV - hemi-octahedron (upper hemisphere) when
        /// <paramref name="hemi"/>, full octahedron (whole sphere) otherwise. Grounded props use hemi; props that
        /// tumble use full. Reuses <see cref="ImpostorSystem"/> so the bake and the runtime sample one mapping.</summary>
        public static Vector3 CellViewDirection(int viewX, int viewZ, int viewsPerAxis, bool hemi)
        {
            Vector2 uv = CellUvCenter(viewX, viewZ, viewsPerAxis);
            return hemi ? ImpostorSystem.DecodeHemiOctahedron(uv) : ImpostorSystem.DecodeOctahedronFull(uv);
        }

        /// <summary>The pixel rectangle cell (<paramref name="viewX"/>,<paramref name="viewZ"/>) owns in the atlas:
        /// a <paramref name="tilePx"/>-square block at (viewX·tile, viewZ·tile). Fails closed to an empty rect.</summary>
        public static RectInt CellPixelRect(int viewX, int viewZ, int tilePx)
        {
            if (tilePx <= 0) return new RectInt(0, 0, 0, 0);
            return new RectInt(viewX * tilePx, viewZ * tilePx, tilePx, tilePx);
        }

        /// <summary>
        /// The whole ordered bake: one <see cref="ImpostorBakeCell"/> per grid cell in row-major order
        /// (tileIndex = viewZ·N + viewX), each carrying its view direction and pixel rect. This fixed order with
        /// no time or random input is why a re-bake reproduces the identical plan. Returns an empty array on
        /// degenerate input.
        /// </summary>
        public static ImpostorBakeCell[] BuildCells(int viewsPerAxis, int tilePx, bool hemi)
        {
            if (viewsPerAxis <= 0 || tilePx <= 0) return System.Array.Empty<ImpostorBakeCell>();
            var cells = new ImpostorBakeCell[viewsPerAxis * viewsPerAxis];
            for (int iz = 0; iz < viewsPerAxis; iz++)
            {
                for (int ix = 0; ix < viewsPerAxis; ix++)
                {
                    int index = iz * viewsPerAxis + ix;
                    cells[index] = new ImpostorBakeCell
                    {
                        viewX = ix,
                        viewZ = iz,
                        tileIndex = index,
                        uvCenter = CellUvCenter(ix, iz, viewsPerAxis),
                        viewDirection = CellViewDirection(ix, iz, viewsPerAxis, hemi),
                        pixelRect = CellPixelRect(ix, iz, tilePx),
                    };
                }
            }
            return cells;
        }

        // ----- The frame-amortised schedule: never render more than a budget per editor frame -----

        /// <summary>Editor frames a bake of <paramref name="totalTiles"/> tiles needs at
        /// <paramref name="tilesPerFrame"/> tiles a frame: ceil(total / perFrame). Fails closed to 0.</summary>
        public static int FramesRequired(int totalTiles, int tilesPerFrame)
        {
            if (totalTiles <= 0 || tilesPerFrame <= 0) return 0;
            return (totalTiles + tilesPerFrame - 1) / tilesPerFrame;
        }

        /// <summary>The half-open tile range [start, start+count) baked on editor frame <paramref name="frameIndex"/>:
        /// a contiguous block of at most <paramref name="tilesPerFrame"/> tiles, clamped so the last frame renders
        /// only the remainder. This is the cap the driver cannot exceed - what keeps the editor responsive. A frame
        /// past the end, or a degenerate input, yields count 0.</summary>
        public static void TilesOnFrame(int frameIndex, int totalTiles, int tilesPerFrame, out int start, out int count)
        {
            start = 0;
            count = 0;
            if (frameIndex < 0 || totalTiles <= 0 || tilesPerFrame <= 0) return;
            int s = frameIndex * tilesPerFrame;
            if (s >= totalTiles) return;
            start = s;
            count = Mathf.Min(tilesPerFrame, totalTiles - s);
        }

        /// <summary>Fraction of a bake complete once <paramref name="tilesDone"/> of <paramref name="totalTiles"/>
        /// tiles are rendered, clamped to [0,1] - the progress the driver reports in its own panel rather than in a
        /// blocking modal bar. Zero total reads as fully done (nothing to bake).</summary>
        public static float Progress01(int tilesDone, int totalTiles)
        {
            if (totalTiles <= 0) return 1f;
            return Mathf.Clamp01((float)tilesDone / totalTiles);
        }

        // ----- The output location: the project's own folder, never an absolute path (C-04) -----

        /// <summary>The project-relative folder impostor atlases are written into. An "Assets/"-rooted path, never
        /// an absolute drive path, so a baked project is portable between machines.</summary>
        public const string AtlasFolderAssetPath = "Assets/UniverseCreator/Content/Impostors";

        /// <summary>
        /// The project-relative asset path for one species' atlas map: the atlas folder, the sanitised species id
        /// and the <paramref name="mapSuffix"/> (e.g. "albedo", "normal"), with a ".png" extension - the atlas is
        /// written as a PNG so that identical pixels encode to identical bytes (the byte-identical re-bake). Always
        /// "Assets/"-rooted; any character not a letter, digit, '-' or '_' in the id or suffix is replaced with
        /// '_' so the id cannot escape the folder or name an illegal file. Fails closed to the folder plus
        /// "_unnamed" on an empty id.
        /// </summary>
        public static string AtlasAssetPath(string speciesId, string mapSuffix)
        {
            string id = Sanitize(speciesId);
            string suffix = Sanitize(mapSuffix);
            if (id.Length == 0) id = "_unnamed";
            string leaf = suffix.Length == 0 ? id : id + "_" + suffix;
            return AtlasFolderAssetPath + "/" + leaf + ".png";
        }

        /// <summary>Replaces every character that is not an ASCII letter, digit, '-' or '_' with '_', so an id or
        /// suffix can never introduce a path separator, a drive prefix or an illegal filename character.</summary>
        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
