// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//                     Module 2: placement solver implemented
//  File:    ScatterNodeBase.cs
//  Purpose: Shared base for the six Scatter nodes.
//
//  Module 1 owned the graph wiring, port contract, parameter sets, and OutputBag
//  channel plumbing. Module 2 fills in the placement solver that was stubbed:
//  blue-noise sampling at minSpacing, density and mask weighting, height and
//  slope rejection, Y snapping, and randomised scale and yaw.
//
//  Per-type prototype selection and species rules remain the subclass's job via
//  PlaceInstances, which now receives a populated list instead of an empty one.
//
//  SEAMLESSNESS
//  ------------
//  The solver is driven from ABSOLUTE world coordinates, not from the tile's
//  index, exactly as the noise creators are. Two neighbouring chunks solved on
//  separate workers therefore agree about the instances near their shared edge,
//  and regenerating a chunk reproduces its layout precisely.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Scatter
{
    [System.Serializable]
    public abstract class ScatterNodeBase : Generator, IOutputNode
    {
        public override NodeCategory Category { get { return NodeCategory.Scatter; } }

        public override Port[] GetInputs() { return SharedPorts.HeightMaskIn; }
        public override Port[] GetOutputs() { return SharedPorts.None; }

        /// <summary>
        /// OutputBag channel these instances are filed under. Module 2/3 consumers
        /// read by this key.
        /// </summary>
        public abstract string Channel { get; }

        /// <summary>
        /// Instances per square kilometre before mask weighting. The fallback figure
        /// that places when the ecology pass is off: with Use Ecology enabled on a
        /// vegetation manager, tree and grass density come from its shipped
        /// per-hectare fields instead (V-13); with ecology off (the shipped default)
        /// this node value is what actually places. See <see cref="CeilingDensityPerSqKm"/>.
        /// </summary>
        public float density = 100f;

        /// <summary>Minimum spacing between instances, in world metres.</summary>
        public float minSpacing = 2f;

        /// <summary>Instances are rejected outside this height band.</summary>
        public float minHeight = 0f;
        public float maxHeight = 1f;

        /// <summary>Instances are rejected outside this slope band, in degrees.</summary>
        public float minSlope = 0f;
        public float maxSlope = 45f;

        /// <summary>Per-node seed so two scatter nodes don't produce identical layouts.</summary>
        public int seed = 1000;

        public Vector2 scaleRange = new Vector2(0.8f, 1.2f);
        public bool randomYaw = true;

        /// <summary>
        /// Softens the height and slope cutoffs so a field thins out over the last
        /// few degrees rather than stopping along a contour line. Slope units are
        /// degrees; the height band uses a fixed small fraction of its own range.
        /// </summary>
        public float slopeFalloff = 5f;

        /// <summary>
        /// Tilts instances toward the surface normal. 0 keeps everything upright,
        /// which is right for trees; 1 lays them flat against the slope, which is
        /// right for rocks and debris.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT named alignToNormal: RockScatterNode and
        /// ObjectScatterNode already ship a serialized bool by that name, and a
        /// same-named base field would hide theirs (CS0108) so their inspector
        /// setting would silently stop reaching the solver. They route their bool
        /// in through <see cref="NormalAlignment"/> instead.
        /// </remarks>
        public float normalAlignment;

        /// <summary>
        /// Effective 0-1 normal alignment the solver applies. Subclasses with their
        /// own alignment control override this to feed it in.
        /// </summary>
        protected virtual float NormalAlignment { get { return normalAlignment; } }

        /// <summary>
        /// Above 0, thins sparse areas so the remaining instances read as groves
        /// and thickets rather than an even field.
        /// </summary>
        public float clusterStrength;

        /// <summary>Neighbourhood radius the cluster test measures density over.</summary>
        public float clusterRadius = 25f;

        /// <summary>
        /// Safety ceiling on instances emitted per chunk. A misconfigured density
        /// on a 1 km chunk can ask for millions, which would exhaust memory before
        /// anything reached the terrain.
        /// </summary>
        public int maxInstancesPerChunk = 50000;

        /// <summary>
        /// False since Module 2: the placement solver is implemented. Subclasses
        /// whose per-type rules are still outstanding override this back to true
        /// so the editor keeps badging them.
        /// </summary>
        public virtual bool WasStubbed { get { return false; } }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            Matrix height = GetInputMatrix(tile, 0, stop);
            Matrix mask = GetInputMatrix(tile, 1, stop);

            if (stop != null && stop.StopRequested) return null;

            List<ScatterInstance> instances = new List<ScatterInstance>();

            // No height field means nothing to stand on. Emitting an empty channel
            // rather than returning early keeps consumers from distinguishing
            // "unwired" from "never ran".
            if (height == null)
            {
                tile.outputs.AddScatter(Channel, instances);
                return null;
            }

            // W1. With ecology active for this tile, resolve the whole-world hydrology
            // field before any species is scored, so the environment sampler reads real
            // shore distance, submersion and salt instead of the empty water probes that
            // no builder ever fills. Cheap after the first chunk - both the drainage
            // solve and the derived field are cached and shared across the bake.
            if (tile.ecology != null)
                tile.ecology.EnsureHydrology(this, tile, stop);

            List<ScatterCandidate> candidates = SolvePlacement(height, mask, tile, stop);

            if (stop != null && stop.StopRequested) return null;

            BuildInstances(candidates, height, tile, instances, stop);

            // Subclass hook. Runs last so per-type rules can filter, duplicate, or
            // re-parameterise what the shared solver produced.
            PlaceInstances(height, mask, tile, instances, stop);

            tile.outputs.AddScatter(Channel, instances);
            return null;
        }

        /// <summary>
        /// Runs the blue-noise solve and returns accepted candidates with their
        /// height, slope, and mask values resolved.
        /// </summary>
        protected virtual List<ScatterCandidate> SolvePlacement(
            Matrix height, Matrix mask, TileData tile, StopToken stop)
        {
            ScatterFilter filter = new ScatterFilter();

            // Only enable a filter that actually excludes something. An enabled
            // slope filter costs a full Sobel pass over the tile, so a node left
            // at the full 0-90 range should not pay for it.
            if (minHeight > 0f || maxHeight < 1f)
                filter.height = RangeFilter.Band(minHeight, maxHeight, 0.02f);

            if (minSlope > 0f || maxSlope < 90f)
                filter.slope = RangeFilter.Band(minSlope, maxSlope, Mathf.Max(0f, slopeFalloff));

            filter.Bind(height, mask);

            PoissonSettings settings = PoissonSettings.Default;

            // Spacing is whichever constraint binds harder: the explicit minimum,
            // or the spacing implied by the requested density. Ignoring the second
            // would let a low density still emit a dense field that is then thinned
            // probabilistically - far more darts thrown for the same result.
            float densitySpacing = PoissonDisc.SpacingForDensity(CeilingDensityPerSqKm(tile));
            settings.minSpacing = Mathf.Max(Mathf.Max(0.01f, minSpacing), densitySpacing);

            // Chunk coordinate does NOT enter the seed. The world cell hash already
            // makes placement position-dependent, and folding the chunk index in
            // would make the same world position hash differently depending on
            // which chunk asked - which is exactly the seam this design avoids.
            settings.seed = seed;
            settings.maxPoints = Mathf.Max(1, maxInstancesPerChunk);

            // StopToken is the node-graph cancellation type; the math layer takes a
            // plain predicate so it stays free of graph types.
            System.Func<bool> isCancelled = null;
            if (stop != null) isCancelled = delegate { return stop.StopRequested; };

            List<ScatterCandidate> candidates = PoissonDisc.Solve(
                settings,
                tile.worldPos.x, tile.worldPos.z,
                tile.worldSize.x, tile.worldSize.z,
                filter.EvaluateWorld,
                isCancelled);

            if (clusterStrength > 0.001f && clusterRadius > 0f)
                candidates = PoissonDisc.ApplyClusterBias(
                    candidates, clusterRadius, clusterStrength, isCancelled);

            // Resolve fills in height, slope, and mask for the survivors only, so
            // rejected darts never pay for the sampling.
            filter.ResolveAll(candidates, isCancelled);
            filter.Unbind();

            return candidates;
        }

        /// <summary>
        /// Turns accepted candidates into placed instances: world position with Y
        /// snapped to the surface, randomised uniform scale, yaw, and optional
        /// alignment to the surface normal.
        /// </summary>
        protected virtual void BuildInstances(
            List<ScatterCandidate> candidates, Matrix height, TileData tile,
            List<ScatterInstance> instances, StopToken stop)
        {
            if (candidates == null) return;

            float scaleMin = Mathf.Min(scaleRange.x, scaleRange.y);
            float scaleMax = Mathf.Max(scaleRange.x, scaleRange.y);

            // A second hash stream, offset from the placement seed so scale and yaw
            // do not correlate with position - reusing candidate.random for all
            // three would make big instances always face the same way.
            Noise variation = new Noise(seed + 7919);

            for (int i = 0; i < candidates.Count; i++)
            {
                if (stop != null && (i & 255) == 0 && stop.StopRequested) return;

                ScatterCandidate c = candidates[i];

                float worldY = tile.worldPos.y + c.height * tile.worldHeight;

                // Quantising to centimetres before hashing keeps the variation
                // stable: a position that differs in the last float bit between
                // two runs would otherwise pick a different scale.
                int hx = Mathf.RoundToInt(c.worldX * 100f);
                int hz = Mathf.RoundToInt(c.worldZ * 100f);

                float rScale = variation.Random(hx, hz);
                float rYaw = variation.Random(hz + 104729, hx + 1299709);

                float scale = Mathf.Lerp(scaleMin, scaleMax, rScale);

                Quaternion rotation = Quaternion.identity;
                if (randomYaw) rotation = Quaternion.Euler(0f, rYaw * 360f, 0f);

                float align = Mathf.Clamp01(NormalAlignment);
                if (align > 0.001f)
                {
                    Vector3 normal = SampleNormal(height, c.worldX, c.worldZ);
                    Quaternion aligned = Quaternion.FromToRotation(Vector3.up, normal) * rotation;
                    rotation = Quaternion.Slerp(rotation, aligned, align);
                }

                instances.Add(new ScatterInstance(
                    new Vector3(c.worldX, worldY, c.worldZ),
                    rotation,
                    new Vector3(scale, scale, scale),
                    0));
            }
        }

        /// <summary>
        /// Instances per square kilometre the densest site may carry (V-13). When the
        /// ecology pass is active for this tile - a vegetation manager present AND its
        /// Use Ecology switch on, which is what makes <see cref="TileData.ecology"/>
        /// non-null - this is the manager's figure, converted from the per-square-metre
        /// ceiling it resolves. With ecology OFF (the shipped default) or no manager,
        /// it is this node's own density field, unchanged from Modules 1 and 2.
        /// </summary>
        /// <remarks>
        /// This is a CEILING. The solve throws darts at the spacing it implies and
        /// the ecology pass then removes the surplus per site, so an ideal hectare
        /// reaches the authored figure and nothing exceeds it. Solving at an average
        /// density instead would need a second, finer solve on good ground, and two
        /// solves do not agree about the same position.
        ///
        /// Why the manager wins for trees WHEN ECOLOGY IS ON: treesPerHectare and
        /// globalDensityScale are shipped inspector fields that until V-13 nothing
        /// read (B-1). A world whose tree count is set on one component and overridden
        /// per node is a world an author cannot reason about, so the component is
        /// authoritative and the node's density is the fallback. NOTE the honest
        /// limit: this hand-off happens only through the ecology context, so with Use
        /// Ecology off the node density below is what actually places, not the
        /// per-hectare dial. (Step 31 audit corrected this comment from claiming
        /// manager PRESENCE alone was enough.)
        /// </remarks>
        protected float CeilingDensityPerSqKm(TileData tile)
        {
            Ecology.EcoScatterContext ctx = tile != null ? tile.ecology : null;
            if (ctx == null) return density;

            float perM2 = Ecology.EcoDensity.CeilingPerSquareMetre(Channel, in ctx.settings, density);
            return perM2 * 1e6f;
        }

        /// <summary>
        /// Surface normal at a world position, by central difference on the height
        /// field. Per-instance rather than a whole-field Sobel pass, because only
        /// the accepted candidates need it.
        /// </summary>
        /// <remarks>
        /// Internal rather than protected so DebrisShaping can reuse it. It is the
        /// only normal implementation in the scatter path and must stay that way -
        /// a second copy would drift from this one silently.
        /// </remarks>
        internal static Vector3 SampleNormal(Matrix height, float worldX, float worldZ)
        {
            if (height == null) return Vector3.up;

            Vector2 ps = height.PixelSize;
            if (ps.x <= 1e-9f || ps.y <= 1e-9f) return Vector3.up;

            Vector2 p = height.WorldToPixel(worldX, worldZ);

            float hl = height.GetInterpolated(p.x - 1f, p.y);
            float hr = height.GetInterpolated(p.x + 1f, p.y);
            float hd = height.GetInterpolated(p.x, p.y - 1f);
            float hu = height.GetInterpolated(p.x, p.y + 1f);

            // Rise is normalised height scaled back to metres; run is two pixels.
            float dx = (hr - hl) * height.worldHeight / (2f * ps.x);
            float dz = (hu - hd) * height.worldHeight / (2f * ps.y);

            return new Vector3(-dx, 1f, -dz).normalized;
        }

        /// <summary>
        /// Per-type placement rules. Receives the accepted candidate list from the
        /// shared solver and may filter, duplicate, or re-parameterise it.
        /// </summary>
        protected abstract void PlaceInstances(Matrix height, Matrix mask, TileData tile,
                                               List<ScatterInstance> instances, StopToken stop);
    }
}
