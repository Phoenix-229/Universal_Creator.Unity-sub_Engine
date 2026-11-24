// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCTonemapping.cs
//  Purpose: The module's tonemapping contract: which curve it asks URP for, and
//           the single low-priority Volume it applies it through. The tonemap
//           itself is URP's own - nothing here or in any surface shader writes a
//           second one - so an author can drop in their own Volume and win.
// -----------------------------------------------------------------------------

using UnityEngine;
#if UC_URP
using UnityEngine.Rendering.Universal;
#endif

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>The two tonemap curves the module will ask URP for. Never None: a
    /// physically exposed HDR scene must be mapped to display or it clips.</summary>
    public enum TonemapPolicy
    {
        Neutral,
        ACES,
    }

    /// <summary>
    /// The module's tonemapping policy, held in one place so the go-live manager
    /// configures a single URP Volume from it. Colour grading is deliberately not
    /// here - a world generator that forces a look is one nobody can ship a game
    /// with, so the module tonemaps and stops.
    /// </summary>
    public static class UCTonemapping
    {
        /// <summary>The authored default. ACES is the filmic choice; an author may replace it.</summary>
        public const TonemapPolicy DefaultPolicy = TonemapPolicy.ACES;

        /// <summary>The module owns at most one Volume. Two would fight over the same override.</summary>
        public const int OwnedVolumeCount = 1;

        /// <summary>
        /// Priority of the module's Volume. Far below an author's default-zero
        /// global Volume, so any Volume they add overrides the module's cleanly
        /// rather than tying with it.
        /// </summary>
        public const float ModuleVolumePriority = -1000f;

#if UC_URP
        /// <summary>Maps the module policy onto URP's own tonemapper. Never returns None.</summary>
        public static TonemappingMode ToUrpMode(TonemapPolicy policy)
        {
            return policy == TonemapPolicy.Neutral ? TonemappingMode.Neutral : TonemappingMode.ACES;
        }
#endif
    }
}
