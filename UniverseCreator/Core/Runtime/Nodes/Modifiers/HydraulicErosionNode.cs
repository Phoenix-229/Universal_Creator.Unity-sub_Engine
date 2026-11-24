// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    HydraulicErosionNode.cs
//  Purpose: Water erosion. Wraps the five-stage solver in Erosion.Hydraulic:
//           height ordering, torrent (flow accumulation), cutting, mudflow
//           transport, and settling.
//
//  This is the single most expensive node in the library and the one that does
//  the most for realism, because it carves drainage networks that are globally
//  consistent rather than locally noisy.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Modifiers
{
    [System.Serializable]
    public class HydraulicErosionNode : ModifierNode
    {
        public override string NodeName { get { return "Hydraulic Erosion"; } }
        public override NodeCategory Category { get { return NodeCategory.Modifier; } }
        public override string Tooltip { get { return "Water erosion. Carves drainage networks and valleys."; } }

        /// <summary>Full erode/settle passes.</summary>
        public int iterations = 3;

        /// <summary>0-1. Rock resistance. Higher erodes less.</summary>
        public float durability = 0.9f;

        /// <summary>Scales how much material each pass removes.</summary>
        public float erosionAmount = 1f;

        /// <summary>Fraction of removed material that becomes transportable mudflow.</summary>
        public float sedimentAmount = 0.5f;

        /// <summary>Mudflow redistribution sub-passes.</summary>
        public int fluidityIterations = 3;

        /// <summary>Post-pass blur sigma in pixels.</summary>
        public float relax = 0.5f;

        // RIV-1 guards (opt-in, default off = today's erosion exactly). From OCG's non-destructive
        // erosion strategy: keep droplet erosion from carving the ocean floor or inventing peaks.
        /// <summary>Floor cells that were ABOVE sea so erosion can never sink dry land below the waterline.</summary>
        public bool respectSeaLevel = false;

        /// <summary>Clamp each cell to at most its ORIGINAL height, so erosion only removes material (no invented peaks).</summary>
        public bool nonDestructive = false;

        /// <summary>Normalised sea height the sea-level guard floors land to (matches the output node's).</summary>
        public float seaLevel = 0.35f;

        // RIV-1b droplet pass (opt-in, default off = today's grid-only erosion exactly).
        /// <summary>
        /// Run a PARTICLE (droplet) pass after the grid solver. The grid solver carves globally
        /// consistent drainage; droplets add the fine rills and alluvial fans it cannot. Off by
        /// default, so every existing world erodes byte-identically.
        /// </summary>
        public bool useDropletPass = false;

        /// <summary>Droplets to simulate at full resolution (scaled down by the coarse factor).</summary>
        public int dropletCount = 20000;

        /// <summary>Radius in cells of the droplet erosion brush.</summary>
        public int dropletBrushRadius = 2;

        /// <summary>
        /// 1 = full resolution. 2+ erodes a downsampled field and adds the smooth delta back,
        /// cutting droplet work by about the square of the factor. This is the mid-flagship
        /// mobile lever; 2 is a good default there.
        /// </summary>
        public int dropletCoarseFactor = 2;

        /// <summary>Spawn seed. The same seed reproduces the droplet pass bit for bit.</summary>
        public int dropletSeed = 0;

        protected override void Modify(Matrix m, TileData tile, StopToken stop)
        {
            // RIV-1: snapshot the source silhouette before eroding, so the guards below can tell
            // what was land and cap the result to the original. Only when a guard is on.
            float[] original = (respectSeaLevel || nonDestructive) ? (float[])m.arr.Clone() : null;

            Erosion.HydraulicSettings settings = new Erosion.HydraulicSettings
            {
                iterations = Mathf.Clamp(iterations, 1, 32),
                durability = Mathf.Clamp01(durability),
                erosionAmount = Mathf.Max(0f, erosionAmount),
                sedimentAmount = Mathf.Clamp01(sedimentAmount),
                fluidityIterations = Mathf.Clamp(fluidityIterations, 1, 16),
                relax = Mathf.Max(0f, relax)
            };

            // The solver probes cancellation once per iteration, so a stop request
            // leaves the field partially eroded but still valid.
            Erosion.Hydraulic(m, settings, null, null, null,
                              stop != null ? (System.Func<bool>)(() => stop.StopRequested) : null);

            // RIV-1b: the droplet pass runs AFTER the grid solver, on the drainage it carved -
            // droplets following an already-consistent network cut rills along the real valleys
            // instead of inventing their own competing ones.
            if (useDropletPass)
            {
                DropletSettings ds = DropletSettings.Default;
                ds.droplets = Mathf.Clamp(dropletCount, 0, 2000000);
                ds.brushRadius = Mathf.Clamp(dropletBrushRadius, 0, 8);
                ds.coarseFactor = Mathf.Clamp(dropletCoarseFactor, 1, 8);
                ds.seed = dropletSeed;

                DropletErosion.Run(m.arr, m.rect.size.x, m.rect.size.z, ds,
                                   stop != null ? (System.Func<bool>)(() => stop.StopRequested) : null);
            }

            if (original != null)
                ApplyErosionGuards(m.arr, original, Mathf.Clamp01(seaLevel), nonDestructive, respectSeaLevel);
        }

        /// <summary>
        /// RIV-1 non-destructive + sea-level guards, as a pure array op so they are directly
        /// testable. <paramref name="nonDestructive"/> caps each cell to its original height (erosion
        /// only ever removes). <paramref name="respectSeaLevel"/> floors a cell that was ABOVE sea
        /// back up to sea if erosion pushed it under - genuine sub-sea ground (original below sea) is
        /// left alone, so the ocean floor keeps its depth.
        /// </summary>
        internal static void ApplyErosionGuards(float[] arr, float[] original, float sea,
                                                bool nonDestructive, bool respectSeaLevel)
        {
            if (arr == null || original == null) return;
            int n = Mathf.Min(arr.Length, original.Length);
            for (int i = 0; i < n; i++)
            {
                float h = arr[i];
                float o = original[i];
                if (nonDestructive && h > o) h = o;                       // no invented peaks
                if (respectSeaLevel && o >= sea && h < sea) h = sea;      // dry land never sinks under the sea
                arr[i] = h;
            }
        }
    }
}
