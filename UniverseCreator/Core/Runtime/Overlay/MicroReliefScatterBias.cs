// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    MicroReliefScatterBias.cs
//  Purpose: The rule that lets the micro-relief field shape WHERE things grow, not
//           only how the ground looks. A scatter site knows two things the overlay
//           already computes: the signed vertical offset the surface was pushed to
//           here (up on a mound, down in a pit), and which named process owns the
//           site (a clast lag, a terracette, a mound field). This class turns those
//           two facts into a suitability multiplier per kind of scattered thing, so a
//           sapling, a tuft of grass and a loose stone each read the SAME relief the
//           shader drew and settle where that relief says they belong - with no
//           authored placement mask anywhere.
//
//  WHY A MULTIPLIER, NOT A HARD MASK  (the done-when has "no extra authored mask")
//  -------------------------------------------------------------------------------
//  A mask says yes/no by hand. This says "more likely / less likely" by physics, so
//  the same field that raised the mound is the thing that also seeds it - one source
//  of truth. The multiplier is 1 at a neutral flat, above 1 where a kind is favoured,
//  below 1 where it is discouraged, never negative. It scales the site's existing
//  suitability; it does not invent placement, so a site the solver already rejected
//  stays rejected.
//
//  THE THREE LAWS THIS ENCODES
//  ---------------------------
//   - SAPLINGS follow DRAINAGE. A convex, raised microsite (mound top, pit shoulder)
//     drains and aerates; a concave hollow ponds and drowns fine roots. So a seedling's
//     odds rise monotonically with relative elevation and fall in the ponded lows -
//     the treefall pit-and-mound signature (L-45) read straight off the offset sign.
//   - GRASS follows FLATNESS. Turf holds a stable, low-gradient bench and thins on a
//     shedding riser, so grass is favoured where the local offset is small, and given
//     an extra edge on solifluction terracette treads (L-37), the classic grass bench.
//   - SMALL STONES follow the LAG. A clast-lag armour (L-33/L-41) or an aeolian granule
//     ripple crest IS the surface where coarse fragments concentrate once fines are
//     winnowed; a loose stone belongs there and is rare on a mound of fines. So stones
//     are gated to those processes rather than to a height.
// -----------------------------------------------------------------------------

namespace UniverseCreator.Overlay
{
    /// <summary>What a scattered thing wants from the ground, physically - the axis the micro-relief bias reads.</summary>
    public enum ScatterMicrositeAffinity
    {
        /// <summary>No microsite preference: the bias is always 1 (the site's own suitability is untouched).</summary>
        Neutral = 0,
        /// <summary>Rooted seedling: favours drained convex microsites, avoids ponded hollows.</summary>
        Sapling = 1,
        /// <summary>Turf-former: favours near-flat benches and terracette treads.</summary>
        Grass = 2,
        /// <summary>Loose coarse clast: belongs in a lag armour / ripple field, rare elsewhere.</summary>
        SmallStone = 3,
    }

    /// <summary>
    /// Pure static map from (what is being scattered, which process owns this site, how far the surface was
    /// displaced) to a suitability multiplier. Device-free and deterministic: the same inputs always give the
    /// same factor, so a self-test can pin mound &gt; flat &gt; pit for a sapling without a scene. The scatter
    /// node multiplies a site's suitability by this once the overlay field is bound; nothing here places or
    /// removes anything by itself.
    /// </summary>
    public static class MicroReliefScatterBias
    {
        // Process ids this bias reacts to by name. They match MicroReliefProcess.ProcessId exactly; a typo here
        // would silently disable a rule, so they are named constants a test can compare against, not literals.
        /// <summary>Solifluction terracette treads (L-37) - the level grass bench between two risers.</summary>
        public const string TerracetteProcessId = "SolifluctionTerracettes";
        /// <summary>Clast-lag armour (L-33/L-41) - coarse fragments left when fines are removed.</summary>
        public const string ClastLagProcessId = "ClastLagArmour";
        /// <summary>Aeolian granule ripples (L-41) - coarse grains ride the crests, a stone's other home.</summary>
        public const string AeolianRippleProcessId = "AeolianRipples";

        // Calibrations. Each is a RATIO grounded in the process, not a free knob: it says how strongly one
        // microsite out-competes a neutral flat. Bounded below 1 so no site is ever zeroed by relief alone -
        // a rare recruit in the "wrong" spot stays possible, which is what real surfaces show.

        /// <summary>How far a fully-drained crest out-competes a flat (and a drowned hollow falls short of it).
        /// 0.6 makes a mound ~1.6x and a deep hollow ~0.4x a flat - a strong, saturating preference, not a veto.</summary>
        private const float SaplingDrainageStrength = 0.6f;

        /// <summary>How far a shedding riser falls short of a level bench for turf. 0.5 makes a full riser ~0.5x a tread.</summary>
        private const float GrassFlatnessStrength = 0.5f;

        /// <summary>Extra edge grass gets on a terracette tread, the bench it colonises first. A modest 1.25x.</summary>
        private const float GrassTreadBonus = 1.25f;

        /// <summary>A coarse clast off any lag/ripple field: rare (a lone dropstone) but not impossible, so 0.15 not 0.</summary>
        private const float StoneOffLagFactor = 0.15f;

        /// <summary>
        /// Suitability multiplier for one scattered thing at one site. <paramref name="offsetM"/> is the signed
        /// vertical displacement the overlay applied here (metres, up positive - a mound is positive, a pit
        /// negative), and <paramref name="referenceAmplitudeM"/> is the local relief scale it is measured against
        /// (the field's own displacement magnitude for the tile), so the response is dimensionless and reads the
        /// same on a centimetre mound as on a metre one. <paramref name="dominantProcessId"/> is
        /// <see cref="MicroReliefField.DominantProcessId"/> for the site, or null where nothing is active.
        /// Returns 1 for a neutral flat, never negative.
        /// </summary>
        public static float SuitabilityMultiplier(
            ScatterMicrositeAffinity affinity, string dominantProcessId, float offsetM, float referenceAmplitudeM)
        {
            // Dimensionless offset: how far up or down this site sits in units of the local relief. If the tile
            // has no relief to speak of, there is no microsite to read and the height-driven rules stay neutral.
            float x = referenceAmplitudeM > 1e-4f ? offsetM / referenceAmplitudeM : 0f;

            switch (affinity)
            {
                case ScatterMicrositeAffinity.Sapling:
                {
                    // Monotone in elevation, saturating: tanh keeps a very tall crest from running away, and a
                    // deep hollow bottoms out at (1 - strength) rather than going negative. Drainage, in one line.
                    float m = 1f + SaplingDrainageStrength * Tanh(x);
                    return m < 0f ? 0f : m;
                }

                case ScatterMicrositeAffinity.Grass:
                {
                    // Favour the flat: the further from level in either direction, the less turf holds.
                    float m = 1f - GrassFlatnessStrength * Abs(Tanh(x));
                    if (dominantProcessId == TerracetteProcessId) m *= GrassTreadBonus; // the bench it owns
                    return m < 0f ? 0f : m;
                }

                case ScatterMicrositeAffinity.SmallStone:
                {
                    // Gate to the lag: a stone belongs where coarse fragments concentrate, not on a height.
                    bool onLag = dominantProcessId == ClastLagProcessId || dominantProcessId == AeolianRippleProcessId;
                    return onLag ? 1f : StoneOffLagFactor;
                }

                default:
                    return 1f; // Neutral: relief does not sort this kind.
            }
        }

        /// <summary>Hyperbolic tangent as float - the bounded, monotone, odd response the height rules ride on.</summary>
        private static float Tanh(float v)
        {
            return (float)System.Math.Tanh(v);
        }

        private static float Abs(float v)
        {
            return v < 0f ? -v : v;
        }
    }
}
