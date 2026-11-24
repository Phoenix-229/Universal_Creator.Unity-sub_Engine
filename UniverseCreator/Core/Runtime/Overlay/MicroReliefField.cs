// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    MicroReliefField.cs
//  Purpose: The micro-relief field itself - the replacement for an octave noise stack.
//           It holds the ordered list of named processes, and at a sample point it asks
//           each eligible one for its millimetres of relief, weights that contribution by
//           how fast the process is currently working, and routes it to geometry or to
//           texture by the Nyquist clamp (L-50). This is the object a debug view drives to
//           solo one process, and the object that lets processes compete for the surface.
//
//  WHY WEIGHT MILLIMETRES BY RATE, NOT JUST SUM THEM
//  -------------------------------------------------
//  Each process returns the relief it physically builds, in millimetres, so contributions
//  are real lengths and could in principle add. But adding all thirteen at full strength
//  averages thirteen landscapes back into noise. Real surfaces instead show whichever
//  process is currently FASTEST, so the field weights each contribution by its activity
//  rate raised to a dominance exponent and normalises: the fastest process claims the
//  surface, the next is legible under it, the rest fade out. The Nyquist split stays the
//  one decision about where a contribution is REPRESENTED (vertex vs normal map), not how
//  large it is - a ripple finer than the vertex grid is not smaller, it is texture.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Overlay
{
    /// <summary>Millimetres of relief split into the two channels the Nyquist clamp routes to.</summary>
    public struct MicroReliefResult
    {
        /// <summary>Signed mm that displaces vertices - coarse enough to resolve as geometry.</summary>
        public float displacementMm;
        /// <summary>Signed mm too fine for the vertex grid - carried by the normal map / splat instead.</summary>
        public float textureMm;
    }

    /// <summary>
    /// Evaluates the sum of named micro-relief processes at a point. One instance is shared and read
    /// concurrently is fine because it holds no per-sample state except the solo filter, which a debug
    /// session sets. Pure per call: the same context yields the same result.
    /// </summary>
    public sealed class MicroReliefField
    {
        private readonly List<MicroReliefProcess> processes = new List<MicroReliefProcess>();

        /// <summary>The processes, in declaration order. The debug view lists these to offer a solo.</summary>
        public IReadOnlyList<MicroReliefProcess> Processes { get { return processes; } }

        /// <summary>When set to a process id, only that process contributes - the debug "solo" switch. Null means all.</summary>
        public string SoloProcessId;

        /// <summary>
        /// Competition sharpness (G-19). Each process is weighted by its activity rate raised to this
        /// exponent, normalised across the eligible set. High values let the single fastest process claim
        /// the surface (a pure signature); low values flatten the weights toward an even blend. The
        /// transition is continuous, so there is no jump between "dominant" and "blended". Default 2.
        /// </summary>
        public float ProcessDominance = 2f;

        /// <summary>Adds a process to the field. Order is preserved but does not affect the sum.</summary>
        public void Add(MicroReliefProcess p)
        {
            if (p != null) processes.Add(p);
        }

        /// <summary>
        /// L-50 Nyquist clamp: a contribution whose wavelength is at least twice the vertex spacing can
        /// be drawn as geometry; anything finer would alias, so it is routed to the texture channel.
        /// </summary>
        public static ReliefChannel Classify(MicroReliefProcess p, in MicroReliefContext c)
        {
            float lambda = p.CharacteristicWavelengthM(c);
            return lambda >= 2f * c.vertexSpacingM ? ReliefChannel.Displacement : ReliefChannel.Texture;
        }

        /// <summary>
        /// G-19 competition: instead of summing every eligible process at full strength (which averages
        /// thirteen landscapes back into noise), weight each by its activity rate raised to
        /// <see cref="ProcessDominance"/> and normalise. The fastest process claims most of the surface,
        /// the next is legible under it, the rest fade out. Two passes over the fixed process list, so it
        /// allocates nothing on the per-vertex path.
        /// </summary>
        public MicroReliefResult Evaluate(in MicroReliefContext c)
        {
            float s = Mathf.Max(0f, ProcessDominance);

            // Pass 1: total competition weight across the eligible (and solo-filtered) processes.
            float totalWeight = 0f;
            for (int i = 0; i < processes.Count; i++)
            {
                var p = processes[i];
                if (SoloProcessId != null && p.ProcessId != SoloProcessId) continue;
                if (!p.IsEligible(c)) continue;
                totalWeight += Weight(p.ActivityRateMmPerYr(c), s);
            }

            var r = new MicroReliefResult();
            if (totalWeight <= 0f) return r; // nothing active here (or every rate sat out)

            // Pass 2: each contribution scaled by its share of the total rate.
            for (int i = 0; i < processes.Count; i++)
            {
                var p = processes[i];
                if (SoloProcessId != null && p.ProcessId != SoloProcessId) continue;
                if (!p.IsEligible(c)) continue;

                float w = Weight(p.ActivityRateMmPerYr(c), s) / totalWeight;
                if (w <= 0f) continue;

                float mm = w * p.DisplacementMm(c);
                if (Classify(p, c) == ReliefChannel.Displacement) r.displacementMm += mm;
                else r.textureMm += mm;
            }
            return r;
        }

        /// <summary>rate^s for a positive rate, else zero - so a dormant process never claims a share even at s=0.</summary>
        private static float Weight(float rateMmPerYr, float sharpness)
        {
            return rateMmPerYr > 0f ? Mathf.Pow(rateMmPerYr, sharpness) : 0f;
        }

        /// <summary>
        /// World-point context provider for <see cref="SampleOffset"/>. Gameplay does not know how to build
        /// a <see cref="MicroReliefContext"/> from a world position - that needs the same litho/cover/env
        /// lookups the overlay assembler feeds the bake - so whoever wires the overlay sets this once, and
        /// then a footstep query is just an (x,z). Null means "no field bound here": SampleOffset returns
        /// zero and gameplay sits on the bare terrain, which is the honest answer when nothing is displaced.
        /// </summary>
        public System.Func<float, float, MicroReliefContext> ContextSampler;

        /// <summary>
        /// The analytic height query gameplay uses so it agrees with the picture: the metres of vertical
        /// offset the overlay shader adds to the terrain at this world point. It is not a second surface -
        /// it runs the SAME <see cref="Evaluate"/> the bake samples, so a foot, a decal, a seated prop or the
        /// camera collision that adds this to the terrain height reads the very surface the shader draws.
        ///
        /// ONLY THE DISPLACEMENT CHANNEL MOVES. The texture channel is sub-Nyquist relief the vertex grid
        /// cannot resolve; the shader carries it in the normal map, not by moving a vertex, so contact must
        /// not chase it either. Returns metres (the field speaks millimetres), signed: up is positive.
        /// </summary>
        public float SampleOffset(float worldX, float worldZ)
        {
            if (ContextSampler == null) return 0f;
            MicroReliefContext c = ContextSampler(worldX, worldZ);
            return Evaluate(c).displacementMm * 0.001f; // mm -> m; geometry channel only, matching the domain shader
        }

        /// <summary>
        /// Index into <see cref="Processes"/> of the process currently claiming the most of the surface here -
        /// the same competition weight <see cref="Evaluate"/> uses, so "dominant" means the one whose signature
        /// the eye actually reads, not merely the first eligible. Returns -1 where nothing is active. This is
        /// what lets scatter ask WHICH process shaped a site (a clast field, a terracette, a mound) rather than
        /// inferring it from an authored mask.
        /// </summary>
        public int DominantProcessIndex(in MicroReliefContext c)
        {
            float s = Mathf.Max(0f, ProcessDominance);
            int best = -1;
            float bestWeight = 0f;
            for (int i = 0; i < processes.Count; i++)
            {
                var p = processes[i];
                if (SoloProcessId != null && p.ProcessId != SoloProcessId) continue;
                if (!p.IsEligible(c)) continue;
                float w = Weight(p.ActivityRateMmPerYr(c), s);
                if (w > bestWeight) { bestWeight = w; best = i; }
            }
            return best;
        }

        /// <summary>The id of the <see cref="DominantProcessIndex"/> process, or null where nothing is active.</summary>
        public string DominantProcessId(in MicroReliefContext c)
        {
            int i = DominantProcessIndex(c);
            return i >= 0 ? processes[i].ProcessId : null;
        }

        /// <summary>
        /// Builds the field with the thirteen standard processes, in the table's order. Order is
        /// documentation only - the sum is order-independent - but it keeps the debug list readable.
        /// </summary>
        public static MicroReliefField CreateDefault()
        {
            var f = new MicroReliefField();
            f.Add(new JointBeddingFacetsProcess());        // L-38
            f.Add(new SpheroidalExfoliationProcess());     // L-39
            f.Add(new KarrenProcess());                    // L-40
            f.Add(new TafoniProcess());                    // L-42
            f.Add(new ClastLagArmourProcess());            // L-33/L-41
            f.Add(new AeolianRipplesProcess());            // L-41
            f.Add(new RainSplashProcess());                // L-43
            f.Add(new RillsProcess());                     // L-44
            f.Add(new SortedPolygonsStripesProcess());     // L-36
            f.Add(new SolifluctionTerracettesProcess());   // L-37
            f.Add(new PitAndMoundProcess());               // L-45
            f.Add(new BioturbationMoundsProcess());        // L-46
            f.Add(new BeachBermWaveNotchProcess());        // L-48
            return f;
        }
    }
}
