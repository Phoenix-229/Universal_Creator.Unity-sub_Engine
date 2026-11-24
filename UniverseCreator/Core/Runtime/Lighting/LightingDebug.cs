// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    LightingDebug.cs
//  Purpose: The visualisation switch that turns a lighting bug from an afternoon into
//           a minute. It publishes one debug mode the lit shaders can branch on, and
//           reads back the state that is otherwise invisible - the tier in force and the
//           analytic exposure - so what the pipeline decided is on screen, not guessed.
//
//  ONE MODE, PUBLISHED ONCE
//  ------------------------
//  The mode is a single global (_UCDebugMode) every UC lit surface can read to isolate
//  one term: ambient only, direct only, AO only, the screen-space GI delta only, the
//  cloud-shadow factor only. Isolating a term is how you tell "the grid is wrong" from
//  "the sun term is wrong" without rebuilding the scene. Off (0) is the shipping path
//  and costs nothing - the branch is a compile-time-cheap compare against a constant.
//
//  READOUTS, NOT GUESSES
//  ---------------------
//  Tier in force comes from the same LightingTier resolution the driver uses, so the
//  overlay cannot disagree with what is actually applied. Exposure is the analytic EV100
//  the sky produced (AtmosphereState.exposureEv), fed in by the driver; shown as "-"
//  until a driver sets it, never faked.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Optimization;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Scene lighting debug overlay. Publishes <c>_UCDebugMode</c> for the lit shaders and
    /// draws an on-screen readout of the tier in force and the current exposure. Inert on
    /// the shipping path (mode Off publishes 0).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Universe Creator/Lighting/Lighting Debug")]
    public sealed class LightingDebug : MonoBehaviour
    {
        /// <summary>The isolate-one-term modes the lit shaders honour. Order is the
        /// published integer, so never renumber without updating the shaders.</summary>
        public enum DebugMode
        {
            Off = 0,
            ProbePositions = 1,
            ProbeColours = 2,
            AmbientOnly = 3,
            DirectOnly = 4,
            AoOnly = 5,
            SsgiOnly = 6,
            CloudShadowOnly = 7,
            ExposureValue = 8,
            TierInForce = 9,
        }

        [Tooltip("Which lighting term to isolate. Off is the shipping path.")]
        public DebugMode mode = DebugMode.Off;

        [Tooltip("Show the text readout (tier in force, exposure, mode) in the top-left.")]
        public bool showReadout = true;

        [Tooltip("Optional. If set, the tier readout reflects this sampler's live tier; " +
                 "otherwise one is found in the scene, falling back to High.")]
        public AdaptiveQuality qualitySource;

        // Grid placement for the probe-lattice gizmo, fed by the driver (matches the
        // shader globals in UC_Lighting.hlsl). Zero spacing => nothing to draw.
        [HideInInspector] public Vector3 gridOrigin;      // world origin of column (0,0), y = base
        [HideInInspector] public float   gridSpacing;     // horizontal spacing (m)
        [HideInInspector] public Vector3Int gridDims;     // column counts

        private static readonly int s_DebugMode = Shader.PropertyToID("_UCDebugMode");

        // Analytic exposure (EV100) the driver last published; NaN until it does.
        private float m_ExposureEv = float.NaN;

        /// <summary>Driver hook: publish the current analytic exposure for the readout.</summary>
        public void SetExposureEv(float ev100) { m_ExposureEv = ev100; }

        private void OnEnable()  { PublishMode(); }
        private void OnDisable() { Shader.SetGlobalFloat(s_DebugMode, 0f); }   // never leave a mode set
        private void OnValidate(){ if (isActiveAndEnabled) PublishMode(); }
        private void Update()    { PublishMode(); }

        private void PublishMode()
        {
            Shader.SetGlobalFloat(s_DebugMode, (float)(int)mode);
        }

        private AdaptiveQuality.QualityTier TierInForce()
        {
            AdaptiveQuality q = qualitySource != null ? qualitySource
                                                      : FindAnyObjectByType<AdaptiveQuality>();
            return q != null ? q.CurrentTier : AdaptiveQuality.QualityTier.High;
        }

        private void OnGUI()
        {
            if (!showReadout) return;

            AdaptiveQuality.QualityTier tier = TierInForce();
            LightingTierSettings s = LightingTier.Resolve(tier);
            string exposure = float.IsNaN(m_ExposureEv) ? "-" : m_ExposureEv.ToString("0.00") + " EV";

            var sb = new System.Text.StringBuilder(160);
            sb.Append("UC Lighting Debug\n");
            sb.Append("Mode:     ").Append(mode).Append('\n');
            sb.Append("Tier:     ").Append(tier).Append('\n');
            sb.Append("Exposure: ").Append(exposure).Append('\n');
            sb.Append("SSGI:     ").Append(s.screenSpaceGI ? "on (" + s.ssgiSampleCount + ")" : "off").Append('\n');
            sb.Append("Contact:  ").Append(s.contactShadows ? "on" : "off").Append('\n');
            sb.Append("Cascades: ").Append(s.cascadeCount).Append(" @ ").Append((int)s.shadowDistance).Append(" m\n");
            sb.Append("Soft:     ").Append(s.softShadows);

            GUI.color = Color.black;
            GUI.Label(new Rect(11f, 11f, 320f, 180f), sb.ToString());
            GUI.color = Color.white;
            GUI.Label(new Rect(10f, 10f, 320f, 180f), sb.ToString());
        }

        private void OnDrawGizmosSelected()
        {
            // Probe-position lattice, drawn only when a grid has been published and one of
            // the probe modes is active. Colours come from the live grid at runtime; here
            // (edit time) we can only place the positions, so draw them as small markers.
            if (gridSpacing <= 0f || gridDims.x < 1 || gridDims.z < 1) return;
            if (mode != DebugMode.ProbePositions && mode != DebugMode.ProbeColours) return;

            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.9f);
            int stepX = Mathf.Max(1, gridDims.x / 32);   // cap the drawn markers
            int stepZ = Mathf.Max(1, gridDims.z / 32);
            for (int z = 0; z < gridDims.z; z += stepZ)
            for (int x = 0; x < gridDims.x; x += stepX)
            {
                Vector3 p = new Vector3(gridOrigin.x + x * gridSpacing,
                                        gridOrigin.y,
                                        gridOrigin.z + z * gridSpacing);
                Gizmos.DrawSphere(p, gridSpacing * 0.08f);
            }
        }
    }
}
