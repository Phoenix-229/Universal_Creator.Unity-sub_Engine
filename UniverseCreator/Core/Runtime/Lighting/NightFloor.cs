// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    NightFloor.cs
//  Purpose: An honest fudge, labelled as one. Physically correct starlight is about a
//           thousandth of a lux - true to the sky, and far too dark to walk a world by.
//           This raises the scene's key illuminance to an authored floor so the default
//           night stays navigable. It is a PLAYABILITY OVERRIDE, NOT PHYSICS: set the
//           floor to zero and the true, near-black new-moon night comes back exactly.
//           The distinction is kept in the open on purpose - a hidden minimum is
//           indistinguishable from a bug to the next reader, and someone always chases it.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// The night-floor override: a minimum key illuminance applied after the physical
    /// photometry, so the default night can be seen and moved through. Zero disables it
    /// and restores the genuine dark; any positive value is an authored, non-physical
    /// lift. <see cref="IsPhysical"/> reports which of the two a given floor is.
    /// </summary>
    public static class NightFloor
    {
        // Small enough to leave the night a night - dimmer than a full moon (0.25 lux),
        // brighter than raw starlight - and large enough to navigate by. Authored, not
        // physical: the physically-true floor is zero.
        public const float DefaultNightFloorLux = 0.05f;

        /// <summary>
        /// Raise a physically-derived key illuminance up to <paramref name="nightFloorLux"/>
        /// (a negative floor is treated as zero). A bright scene is never lowered; a floor of
        /// zero returns the physical value untouched - a genuinely black new-moon night.
        /// </summary>
        public static float Apply(float keyIlluminanceLux, float nightFloorLux)
        {
            return Mathf.Max(keyIlluminanceLux, Mathf.Max(0f, nightFloorLux));
        }

        /// <summary>
        /// True only when the floor is zero - the one setting that leaves the night
        /// physically honest. Any positive floor is a playability override, not physics.
        /// </summary>
        public static bool IsPhysical(float nightFloorLux)
        {
            return nightFloorLux <= 0f;
        }
    }
}
