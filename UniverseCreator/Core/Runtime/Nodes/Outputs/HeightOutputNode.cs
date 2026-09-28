// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    HeightOutputNode.cs
//  Purpose: Writes the height matrix into OutputBag.heightmap for Unity's
//           TerrainData.SetHeights. The canonical output node every working
//           graph must have.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Outputs
{
    [System.Serializable]
    public class HeightOutputNode : OutputNode
    {
        public override string NodeName { get { return "Height Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }
        public override string Tooltip { get { return "Write the height matrix to TerrainData."; } }

        public float heightScale = 600f;

        // -- Core sea-level floor -------------------------------------------------
        // Baked into the output writer rather than offered as a node, so it runs for
        // every world no matter how the graph is wired - and because it floors the
        // COMPOSED heightmap, it binds ALL base noise creators AND every combiner at
        // once. It lifts dry land that has fallen below sea level back ABOVE it (killing
        // the "continent below the sea" and river-levee-wall/dam-bank artifacts), while
        // leaving genuine ocean and closed lake basins alone so the sea keeps its depth.
        // seaFreeboardMetres raises lifted dry land STRICTLY above the waterline, never onto
        // it, by a real-world SAFETY margin (metres). It is a guard, not the shape: the
        // natural terrain math still decides where land eases down to the sea, but no dry
        // ground - and above all no flat plain - is ever left sitting AT or below the
        // waterline; a lifted dip clears the sea by at least this many metres. Expressed in
        // metres and converted through heightScale so the 20 m guarantee holds at any world
        // height. See SeaLevelFloor.cs for the ocean/basin classification it never touches.
        [System.NonSerialized] public bool enforceSeaLevelFloor = true;
        public float seaLevel = 0.35f;         // normalised waterline (system default)
        public float coastGrade = 0.03f;       // height band a lifted cell eases back over
        public float basinDepth = 0.01f;       // pooled depth above which a dip is a real basin
        public float seaFreeboardMetres = 20f; // METRES of clearance lifted dry land keeps above sea (safety guard)
        public float floorStrength = 1f;       // 0..1 blend of the lift

        // -- Core coastal shelf ---------------------------------------------------
        // Runs in the same output post-pass, right after the sea-level floor and on the
        // same composed heightmap, so it too binds every base creator AND combiner at
        // once with no wiring. Where land meets the ocean it grades the GENTLE stretches
        // into beaches and coastal plains and leaves the STEEP ones standing as cliffs,
        // so the coast is varied instead of a boxy wall of hill dropping into deep water.
        // Land-side and lower-only, so it never touches the sea's own depth and never
        // builds a wall. See CoastalShelf.cs for the slope + regional-character gating.
        [System.NonSerialized] public bool enforceCoastalShelf = true;
        public float coastalRange = 500f;       // metres inland the coastal band reaches (ascent edge)
        public float beachFlat = 10f;           // metres of near-shore wet beach sitting at the waterline
        public float coastalPlainMetres = 40f;  // metres of FLAT coastal-plain rim beyond the beach
        public float coastalRise = 0.03f;       // normalised height the beach ramp rises onto the rim
        public float cliffThreshold = 0.05f;    // excess above the ramp beyond which a shore stays a cliff
        public float coastalVariety = 0.6f;     // 0..1 regional variation between beach / plain / hill
        public float coastalShelfStrength = 1f; // 0..1 blend of the grade

        protected override void WriteOutput(Matrix src, TileData tile, StopToken stop)
        {
            // LOW layer: floor the base terrain before it becomes the heightmap. Rivers,
            // lakes and the ocean are stamped later in the settle phase and sit on top of
            // this, so the order is base-clamp -> carve -> water exactly as intended.
            bool wantFloor = enforceSeaLevelFloor && floorStrength > 1e-5f;
            bool wantShelf = enforceCoastalShelf && coastalShelfStrength > 1e-5f;
            if (wantFloor || wantShelf)
            {
                // One whole-world drainage solve serves both passes; the per-bake cache
                // means later chunks reuse it byte-for-byte rather than re-solving.
                WorldDrainage wd = WorldDrainage.Solve(this, 0, tile,
                    SeaLevelFloor.DefaultResolution, 0f, false, stop);
                if (stop != null && stop.StopRequested) return;

                if (wd != null)
                {
                    // Work on a copy: src may be a cached product other nodes still read.
                    Matrix work = new Matrix(src);

                    if (wantFloor)
                    {
                        // Convert the metres guard into the normalised height space the floor
                        // works in: normalised = metres / heightScale, so "20 m above sea"
                        // means the same lift whatever the world's vertical scale is.
                        float scaleM = heightScale > 1e-3f ? heightScale : 600f;
                        float seaFreeboardNorm = Mathf.Clamp01(seaFreeboardMetres / scaleM);

                        SeaLevelFloor.Apply(wd, work, seaLevel, coastGrade,
                                            basinDepth, seaFreeboardNorm, floorStrength, stop);
                        if (stop != null && stop.StopRequested) return;
                    }

                    if (wantShelf)
                    {
                        // After the floor: dry ground is now guaranteed >= sea, so the shelf
                        // only has to grade that coast into beach / plain, leaving cliffs.
                        CoastalShelf.Apply(wd, work, seaLevel, coastalRange, beachFlat,
                                           coastalPlainMetres, coastalRise, cliffThreshold,
                                           coastalVariety, coastalShelfStrength, stop);
                        if (stop != null && stop.StopRequested) return;
                    }

                    src = work;
                }
            }

            int w = src.rect.size.x;
            int h = src.rect.size.z;

            // Unity's SetHeights expects [z, x] with z running north.
            float[,] heightmap = new float[h, w];

            for (int z = 0; z < h; z++)
            {
                if (stop != null && stop.StopRequested) return;

                int rowStart = z * w;
                for (int x = 0; x < w; x++)
                {
                    float v = src.arr[rowStart + x];

                    // NaN and infinity are tested for by name, not clamped. Clamp01 is
                    // two inequalities and an else, and NaN fails both inequalities, so
                    // a clamp here passed every NaN straight through to SetHeights - the
                    // needle through the terrain. This runs off-thread on a buffer the
                    // graph already owns, so it is the cheapest place to stop one: the
                    // whole terrain path downstream then only ever sees real numbers.
                    //
                    // Zero rather than a neighbour: at this point there is no context to
                    // say what the surface was meant to do, and HeightGuard runs the
                    // neighbourhood repair on the main thread where there is.
                    if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;

                    heightmap[z, x] = v < 0f ? 0f : (v > 1f ? 1f : v);
                }
            }

            tile.outputs.heightmap = heightmap;
            tile.outputs.requestColliderRebuild = true;
        }
    }
}
