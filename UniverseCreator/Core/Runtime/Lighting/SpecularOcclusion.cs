// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    SpecularOcclusion.cs
//
//  KEPT AS A TEST ORACLE, NOT AN ORPHAN (verdict 2026-10-10). No runtime C# calls this, but it
//  is NOT dead: UC_SurfaceReflectance.hlsl ("the shader mirrors of SpecularOcclusion.cs and
//  WetSurfaceOptics.cs - the SAME maths") ships that logic on terrain, vegetation and rock, and
//  the self-test runs THIS as the CPU reference the shader is checked against (the drift guard).
//  Deleting it would delete that coverage of a shipped feature. Keep; do not "wire" (wiring it at
//  runtime would duplicate the shader on the CPU). See SUBSYSTEM_WIRING_FIX.md.
//
//  Purpose: Keep the ambient sky highlight out of the cracks. Ambient specular from the sky is
//           bright and, left alone, it does not care about geometry - so every crevice, seam and
//           contact gap picks up a rim of sky reflection it should never see, and the scene looks
//           worse with "GI on" than off. The fix is to occlude the specular the same way S-71
//           occludes the diffuse, but over a NARROWER cone: the specular lobe gathers from a tight
//           set of directions around the mirror reflection, not the whole hemisphere, so it must be
//           judged by whether THAT direction is open, not by how much of the sky is open overall.
//           The dominant reflected direction is tested against S-71's bent normal - the average
//           open direction - and the cone widens with roughness until, for a fully rough surface,
//           the specular occlusion collapses back to the diffuse visibility. A crevice, whose bent
//           normal tilts out of the gap and whose sky visibility is low, then shows no interior
//           highlight, while the same rock's open top still catches the sky.
//
//           Pure and deterministic: the caller supplies S-71's visibility and bent normal, the
//           reflected direction and the roughness; this owns only the cone narrowing.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Lighting
{
    /// <summary>
    /// Specular ambient occlusion (S-75). The diffuse sky visibility from S-71, narrowed to the
    /// specular lobe: a smooth surface is judged by whether its dominant reflected direction points
    /// at open sky (its alignment with the bent normal), a rough surface by the full diffuse
    /// visibility, and everything between by roughness. Returns a specular VISIBILITY in 0..1
    /// (1 = the reflection sees open sky, 0 = fully occluded, no highlight). All static, thread-safe.
    /// </summary>
    public static class SpecularOcclusion
    {
        /// <summary>
        /// The share of the ambient sky specular that survives geometry. This is the Frostbite
        /// specular-occlusion approximation with the dominant reflected direction taken against
        /// S-71's bent normal: <c>saturate(pow(align + vis, 2^(-16 r - 1)) - 1 + vis)</c>, where
        /// <paramref name="diffuseVisibility01"/> is S-71's open fraction and <c>align</c> is how
        /// far the reflected direction leans toward the open sky (its bent normal). At full
        /// roughness the exponent drives the power term to one and the result collapses to the
        /// diffuse visibility - the specular cone has widened to the whole hemisphere. At zero
        /// roughness the narrow cone is gated by alignment: a mirror facing the open sky keeps its
        /// highlight, a mirror facing into a crevice loses it.
        /// </summary>
        public static float Evaluate(float diffuseVisibility01, Vector3 bentNormal,
                                     Vector3 reflectDir, float roughness01)
        {
            float vis = Mathf.Clamp01(diffuseVisibility01);
            float r = Mathf.Clamp01(roughness01);

            // How far the dominant reflected direction leans toward the open sky (the bent normal).
            Vector3 b = bentNormal.sqrMagnitude > 1e-12f ? bentNormal.normalized : Vector3.up;
            Vector3 d = reflectDir.sqrMagnitude > 1e-12f ? reflectDir.normalized : Vector3.up;
            float align = Mathf.Clamp01(Vector3.Dot(d, b));

            // The cone width: smooth -> tight exponent (alignment matters), rough -> exponent ~1
            // so the power term vanishes and the whole expression relaxes to the diffuse visibility.
            float exponent = Mathf.Pow(2f, -16f * r - 1f);
            float f = Mathf.Pow(align + vis, exponent) - 1f + vis;
            return Mathf.Clamp01(f);
        }
    }
}
