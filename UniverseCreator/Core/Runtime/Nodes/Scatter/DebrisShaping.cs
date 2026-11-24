// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    DebrisShaping.cs
//  Purpose: V-44. The ONE place a V-19 deposit becomes instance transforms.
//
//  WHY THIS FILE EXISTS RATHER THAN THREE COPIES OF IT
//  ---------------------------------------------------
//  Three channels place loose stone - objects, rocks and debris - and before this
//  file each one carried its own hand-authored version of the same four decisions:
//
//    RockScatterNode    embedDepth, randomTilt          how a stone sits
//    DebrisScatterNode  clumping, clumpRadius, cavity   where stone collects
//    ObjectScatterNode  weighted roulette               which stone
//
//  DebrisPlacement.Resolve already answers all four, and answers them by process
//  instead of by a constant: density from whether a process delivered material,
//  size from how that material grades, alignment and embedding from how it came to
//  rest. So the three authored rule sets were three different codes doing one job -
//  the duplication C-15 forbids. They now all call Apply below, and the authored
//  fields survive only as the no-ecology fallback.
//
//  WHAT "NO STONE IS AUTHORED BY HAND" MEANS, PRECISELY
//  ---------------------------------------------------
//  It means no stone's POSITION, SIZE, ORIENTATION or ABUNDANCE is authored. Those
//  are the outputs of a process here. It does not mean the author is forbidden to
//  say which mesh is a boulder - somebody has to bind a prototype slot to a talus
//  block, and no amount of physics can infer that from a mesh. That binding is the
//  kindBinding array: nine slots, one per DebrisKind, and it is asset binding
//  rather than placement authoring. An unbound kind places nothing, which is the
//  honest answer - it means "this world has no mesh for river cobble", not "put
//  something else there".
//
//  THE THINNING IS A HASH, NOT A RANDOM, for the reason EcoDensity states: which
//  surplus piece goes must be the same answer on every rebuild and on every worker,
//  and a sequential draw makes it depend on processing order.
//
//  EMBEDDING SINKS ALONG WORLD DOWN, NOT ALONG THE NORMAL. On a steep face a
//  normal-aligned offset slides the piece sideways out of the hollow it was placed
//  in, which reads as a boulder floating beside the slope. RockScatterNode already
//  had this right and the reasoning is inherited from it verbatim.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Ecology;

namespace UniverseCreator.Nodes.Scatter
{
    /// <summary>
    /// Turns V-19 deposits into instance transforms. Static and allocation free;
    /// safe from a worker thread.
    /// </summary>
    public static class DebrisShaping
    {
        /// <summary>One slot per <see cref="DebrisKind"/>, including None at 0.</summary>
        public const int KindCount = 9;

        /// <summary>
        /// Offset for the thinning hash stream. Decorrelated from the base solver's
        /// scale and yaw streams so a piece kept by the thinning is not systematically
        /// the piece the solver happened to make large.
        /// </summary>
        private const int ThinStream = 71234587;

        /// <summary>Offset for the isolated-deposit draw Resolve needs.</summary>
        private const int SiteStream = 39916801;

        /// <summary>
        /// Rewrites <paramref name="instances"/> in place as the deposit at each
        /// position, removing the pieces no process delivered. Returns false when the
        /// site cannot be interrogated at all, so the caller can keep its pre-Module-3
        /// behaviour rather than emptying the channel.
        /// </summary>
        /// <param name="kindBinding">Prototype index per DebrisKind, or null to leave
        /// each instance's existing index alone. A negative entry means the world has
        /// no mesh for that kind and the piece is dropped.</param>
        public static bool Apply(TileData tile, Matrix height, int seed,
                                 List<ScatterInstance> instances,
                                 int[] kindBinding, StopToken stop)
        {
            if (instances == null || instances.Count == 0) return false;

            EcoScatterContext ctx = tile != null ? tile.ecology : null;
            if (ctx == null) return false;

            ctx.EnsureBound(height);
            if (!ctx.CanSelect()) return false;

            // Not an authored setting. EcoSampleSettings carries no glaciation field,
            // and inventing one here would add a serialised field nothing else reads.
            // The constant is the measured Pleistocene figure; when the climate layer
            // owns a real glaciation history this is the single line that changes.
            float glaciationM = EcoConstants.GlaciationAltitudeMetres;

            Noise thin = new Noise(seed + ThinStream);
            Noise siteDraw = new Noise(seed + SiteStream);

            int write = 0;

            for (int i = 0; i < instances.Count; i++)
            {
                if (stop != null && (i & 255) == 0 && stop.StopRequested) return true;

                ScatterInstance inst = instances[i];

                // Centimetre quantisation before hashing, matching the base solver:
                // a position differing in the last float bit between two runs must not
                // change whether a stone is here.
                int hx = Mathf.RoundToInt(inst.position.x * 100f);
                int hz = Mathf.RoundToInt(inst.position.z * 100f);

                EnvironmentSample site = ctx.sampler.Sample(inst.position.x, inst.position.z);

                DebrisForm form = DebrisPlacement.Resolve(
                    in site, siteDraw.Random(hx, hz), glaciationM);

                // No process delivers material here. Not a fallback case - an answer.
                if (form.kind == DebrisKind.None) continue;

                if (!EcoDensity.KeepAtSite(form.density, thin.Random(hx, hz))) continue;

                if (kindBinding != null)
                {
                    int k = (int)form.kind;
                    if (k < 0 || k >= kindBinding.Length) continue;

                    int proto = kindBinding[k];

                    // Unbound kind: this world has no mesh for it, so nothing is
                    // placed. Substituting another kind's mesh would put river cobble
                    // on a cliff and call it geomorphology.
                    if (proto < 0) continue;

                    inst.prototypeIndex = proto;
                }

                // V-34. How strongly the process delivered this piece IS its fitness,
                // so stone takes part in the downstream budget instead of sitting at
                // the unscored 0 that exempts it. A talus block on ground at repose
                // outranks one clinging near the limit, and when the budget has to drop
                // instances it drops the marginal stone first rather than by emission
                // order. Clamped off zero so a kept piece is never reported unscored.
                inst.suitability = Mathf.Max(1e-4f, Mathf.Clamp01(form.density));

                // ---- grading -------------------------------------------------
                // Multiplied into the solver's scale rather than replacing it, so the
                // prototype's authored size range still applies and the grading rides
                // on top of it. Replacing it would make every talus block identical.
                if (form.sizeFactor > 0f && !Mathf.Approximately(form.sizeFactor, 1f))
                {
                    Vector3 s = inst.scale;
                    s.x *= form.sizeFactor;
                    s.y *= form.sizeFactor;
                    s.z *= form.sizeFactor;
                    inst.scale = s;
                }

                // ---- how it lies ---------------------------------------------
                inst.rotation = Orient(height, in inst, in form, hx, hz, thin);
                // UC-T06-07: deposit shaping owns its resting tilt; final contact must not apply the slope twice.
                inst.ground = new TerrainSystem.ScatterGroundBinding(0f, 0f, inst.rotation,
                    ScatterNodeBase.SampleNormal(height, inst.position.x, inst.position.z), form.normalAlignment);
                inst.ground.usesPrototypeAlignment = false;

                // ---- how deep it sits ----------------------------------------
                // Straight down, scaled by the piece's own height so a large block
                // buries proportionally rather than by a fixed metre figure.
                if (form.embedFraction > 0f)
                {
                    inst.position = new Vector3(
                        inst.position.x,
                        inst.position.y - form.embedFraction * inst.scale.y,
                        inst.position.z);
                }

                instances[write++] = inst;
            }

            // Compact in place. The list is reused per chunk, so trimming beats
            // allocating a second list per channel per chunk.
            if (write < instances.Count)
                instances.RemoveRange(write, instances.Count - write);

            return true;
        }

        /// <summary>
        /// The resting attitude: ground normal blended by the deposit's own alignment,
        /// then yawed. A yaw of -1 means the piece may face anywhere, so it takes a
        /// hashed compass bearing instead of a process-imposed one.
        /// </summary>
        private static Quaternion Orient(Matrix height, in ScatterInstance inst,
                                         in DebrisForm form, int hx, int hz, Noise noise)
        {
            float yaw = form.yawDeg >= 0f
                      ? form.yawDeg
                      : noise.Random(hz + 15485863, hx + 49979687) * 360f;

            Quaternion spin = Quaternion.Euler(0f, yaw, 0f);

            float align = Mathf.Clamp01(form.normalAlignment);
            if (align <= 0f || height == null) return spin;

            // The base solver's own central-difference normal, reused rather than
            // recomputed. A second copy of that math here is exactly the duplication
            // this file exists to remove, and the two would drift apart.
            Vector3 n = ScatterNodeBase.SampleNormal(height, inst.position.x, inst.position.z);

            // Slerped from upright toward the surface, so alignment is a dial rather
            // than a switch: talus at 0.85 lies almost with the slope, a wedged
            // erratic at 0.2 stays nearly upright.
            Quaternion lie = Quaternion.FromToRotation(Vector3.up, n);

            return Quaternion.Slerp(Quaternion.identity, lie, align) * spin;
        }
    }
}
