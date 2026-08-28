// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    LightingProbeVolume.cs
//  Purpose: An authored bounds override that densifies the irradiance grid inside a
//           region that matters - a village, a canyon, a cave mouth - WITHOUT raising
//           the global probe spacing and paying for it everywhere.
//
//  WHY A VOLUME AND NOT A GLOBAL DIAL
//  ----------------------------------
//  The grid's base spacing (S-40) is a whole-world cost. Halving it to capture the
//  bounce detail of one narrow canyon quadruples probe count across an entire terrain
//  the player mostly sees from open ground. A volume says "spend the probes HERE": the
//  grid builder asks each world column whether a volume covers it and, if so, tightens
//  the spacing by that volume's scale. Outside every volume the base spacing stands.
//
//  DENSEST WINS
//  ------------
//  Where volumes overlap, the smallest scale (the densest request) is the one honoured -
//  an author who dropped a tight volume inside a looser one meant the tight one. This is
//  a registry the grid builder reads; the component itself only declares intent.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// A world-space box that requests denser irradiance probes inside it. Add it to a
    /// scene, size it over the region, and set how much tighter the spacing should be.
    /// Purely declarative: the grid builder queries <see cref="TryGetSpacingScale"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Universe Creator/Lighting/Lighting Probe Volume")]
    public sealed class LightingProbeVolume : MonoBehaviour
    {
        [Tooltip("World-space size of the box, centred on this transform (metres).")]
        public Vector3 size = new Vector3(40f, 20f, 40f);

        [Tooltip("Spacing multiplier inside the box. 0.5 = probes twice as dense as the " +
                 "global spacing; 1 = no change. Lower is denser and costs more probes.")]
        [Range(0.1f, 1f)] public float spacingScale = 0.5f;

        // Every enabled volume in the scene. The grid builder reads this at build time.
        private static readonly List<LightingProbeVolume> s_Active = new List<LightingProbeVolume>();

        /// <summary>Read-only view of the active volumes, for the grid builder and debug.</summary>
        public static IReadOnlyList<LightingProbeVolume> Active { get { return s_Active; } }

        /// <summary>World-space bounds of this volume this frame.</summary>
        public Bounds WorldBounds
        {
            get
            {
                Vector3 s = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
                return new Bounds(transform.position, s);
            }
        }

        private void OnEnable()
        {
            if (!s_Active.Contains(this)) s_Active.Add(this);
        }

        private void OnDisable()
        {
            s_Active.Remove(this);
        }

        /// <summary>
        /// The densest spacing scale requested at a world position, or false if no volume
        /// covers it (the caller then keeps the global base spacing). Densest (smallest
        /// scale) wins on overlap. XZ-only test on Y is intentional: the grid follows the
        /// surface, so a volume covers a column when its horizontal footprint does and its
        /// vertical extent straddles the terrain there.
        /// </summary>
        public static bool TryGetSpacingScale(Vector3 worldPos, out float scale)
        {
            scale = 1f;
            bool covered = false;
            for (int i = 0; i < s_Active.Count; i++)
            {
                LightingProbeVolume v = s_Active[i];
                if (v == null) continue;
                if (v.WorldBounds.Contains(worldPos))
                {
                    float s = Mathf.Clamp(v.spacingScale, 0.1f, 1f);
                    if (!covered || s < scale) { scale = s; covered = true; }
                }
            }
            return covered;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.25f);
            Gizmos.DrawCube(transform.position, WorldBounds.size);
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.9f);
            Gizmos.DrawWireCube(transform.position, WorldBounds.size);
        }
    }
}
