// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    GovernorStatus.cs
//  Purpose: The governor is advisory to the author, never silent (§ 9.10 G-66). It must
//           report the active tier, whether it was reduced automatically, and WHY, in the
//           inspector and the debug window - and the author must be able to pin the tier to
//           defeat the auto-reduction. A build that quietly runs at Mobile Low on a flagship
//           because of one hot afternoon is a support ticket nobody can reproduce; the status
//           line is what turns that from a mystery into a visible, explained decision.
//
//  A PIN DEFEATS THE AUTO-REDUCTION
//  --------------------------------
//  When the author pins a tier, the effective tier is the AUTHORED one regardless of what the
//  governor decided - `EffectiveTier` returns the authored tier when pinned, the auto-reduced
//  tier otherwise. The status string names both when they differ ("Mobile High, auto-reduced
//  from Desktop") and gives the reason ("thermal drift 18 %"), exactly as the done-when reads.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Enums and a formatted string; no inspector, no GUI. The scene layer draws the string this
//  produces. The self-test pins the tier display names, the pin-defeats-reduction rule, and the
//  verbatim status line.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>Why the governor reduced the tier.</summary>
    public enum ReductionReason { None = 0, Budget = 1, ThermalDrift = 2 }

    /// <summary>
    /// The advisory status of the geometry governor (§ 9.10 G-66): the active tier, whether it was auto-reduced,
    /// the reason, and the pin that defeats the reduction. Reuses <see cref="SplatCacheTier"/> for tier identity.
    /// Device-free; the inspector/debug window renders the string.
    /// </summary>
    public static class GovernorStatus
    {
        /// <summary>The human-readable tier name ("Mobile Low", "Mobile High", "Desktop", "Ultra").</summary>
        public static string DisplayName(SplatCacheTier tier)
        {
            switch (tier)
            {
                case SplatCacheTier.MobileLow: return "Mobile Low";
                case SplatCacheTier.MobileHigh: return "Mobile High";
                case SplatCacheTier.Desktop: return "Desktop";
                case SplatCacheTier.Ultra: return "Ultra";
                default: return tier.ToString();
            }
        }

        /// <summary>The tier actually in force: the authored tier when the author has pinned it (defeating the
        /// governor), otherwise the auto-reduced tier.</summary>
        public static SplatCacheTier EffectiveTier(SplatCacheTier authoredTier, SplatCacheTier autoReducedTier, bool pinned)
        {
            return pinned ? authoredTier : autoReducedTier;
        }

        /// <summary>The reason clause: "thermal drift 18 %", "over budget", or empty for None.</summary>
        private static string ReasonClause(ReductionReason reason, float driftPercent)
        {
            switch (reason)
            {
                case ReductionReason.ThermalDrift: return "thermal drift " + Mathf.RoundToInt(driftPercent) + " %";
                case ReductionReason.Budget: return "over budget";
                default: return "";
            }
        }

        /// <summary>The advisory status line. When not auto-reduced: "Tier: Desktop". When auto-reduced:
        /// "Tier: Mobile High (auto-reduced from Desktop — thermal drift 18 %)".</summary>
        public static string Format(SplatCacheTier activeTier, SplatCacheTier authoredTier,
                                    bool autoReduced, ReductionReason reason, float driftPercent)
        {
            if (!autoReduced || activeTier == authoredTier)
                return "Tier: " + DisplayName(activeTier);

            string clause = ReasonClause(reason, driftPercent);
            string why = clause.Length > 0 ? " — " + clause : "";
            return "Tier: " + DisplayName(activeTier) + " (auto-reduced from " + DisplayName(authoredTier) + why + ")";
        }
    }
}
