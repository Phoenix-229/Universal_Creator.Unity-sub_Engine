// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    UCShaderDiagnostics.cs
//  Purpose: Reports compile errors for the shaders this module ships.
//
//  WHY THIS IS NOT REDUNDANT WITH "THE IMPORT SUCCEEDED"
//  ----------------------------------------------------
//  Importing a .shader parses it and queues variant compilation; it does not
//  compile it. A shader with a genuine HLSL error imports quietly, logs nothing
//  at import time, and only turns magenta when something first renders it - by
//  which point the failure looks like a material or pipeline problem rather
//  than a syntax error twenty lines into an include.
//
//  ShaderUtil holds the messages the compiler produced. Asking it directly is
//  the only way to know a shader is actually sound from batch mode, where
//  nothing ever renders.
// -----------------------------------------------------------------------------

using System.Text;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor.Diagnostics
{
    public static class UCShaderDiagnostics
    {
        /// <summary>Every shader this module owns and must keep compiling.</summary>
        public static readonly string[] ShipShaders =
        {
            "UniverseCreator/TerrainLit",
            "UniverseCreator/Water",
            "UniverseCreator/WindDetail",
            "UniverseCreator/VegetationLit",
            "UniverseCreator/RockLit",
            "UniverseCreator/Rain",
            "UniverseCreator/Snow",
            "UniverseCreator/Fog",
            "UniverseCreator/Skybox",
            "UniverseCreator/Clouds",
            "UniverseCreator/SunShafts",
            "Hidden/UniverseCreator/WaterRipple",
            "Hidden/UniverseCreator/ContactShadow",
            "Hidden/UniverseCreator/SSGI",

            // § 9 Living World geometry shaders. TerrainDetailTess carries the G-24
            // no-tessellation fallback as a second sub-shader; Impostor carries the
            // hemi/full and season keyword variants; VirtualSplatSample compiles the
            // UC_VirtualSplat.hlsl page-table sample include.
            "UniverseCreator/TerrainDetailTess",
            "UniverseCreator/Impostor",
            "Hidden/UniverseCreator/VirtualSplatSample"
        };

        /// <summary>
        /// Required structural variants a shipped shader must keep. A shader whose actual
        /// sub-shader count or keyword set falls below what it declares here has silently
        /// lost a variant - the G-24 tessellation fallback deleted, the impostor's hemi/full
        /// or season keyword dropped - and the check fails the build rather than shipping a
        /// shader that renders wrong or magenta on the path that lost its variant.
        /// </summary>
        private struct VariantRequirement
        {
            public int minSubshaders;      // e.g. TerrainDetailTess needs its 4.6 pass AND the G-24 fallback
            public string[] mustHaveKeywords; // local keywords that a dropped multi_compile would remove
        }

        private static VariantRequirement RequirementFor(string shaderName)
        {
            switch (shaderName)
            {
                case "UniverseCreator/TerrainDetailTess":
                    // The SM 4.6 tessellated sub-shader plus the G-24 fixed-mesh fallback.
                    return new VariantRequirement { minSubshaders = 2, mustHaveKeywords = null };
                case "UniverseCreator/Impostor":
                    return new VariantRequirement
                    {
                        minSubshaders = 1,
                        mustHaveKeywords = new[] { "_UC_IMPOSTOR_FULL", "_UC_IMPOSTOR_SEASON" }
                    };
                default:
                    return new VariantRequirement { minSubshaders = 1, mustHaveKeywords = null };
            }
        }

        [MenuItem("Tools/Universe Creator/Diagnostics/Check Shaders", priority = 210)]
        public static void CheckMenu()
        {
            string report;
            bool ok = Check(out report);
            if (ok) Debug.Log("[UC] Shader check passed.\n" + report);
            else    Debug.LogError("[UC] Shader check FAILED.\n" + report);
        }

        /// <summary>Batch entry point. Exits non-zero when a shader has errors.</summary>
        public static void RunBatch()
        {
            string report;
            bool ok = Check(out report);
            Debug.Log("=== UC SHADER CHECK: " + (ok ? "PASS" : "FAIL") + " ===\n" + report);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        public static bool Check(out string report)
        {
            var sb = new StringBuilder();
            bool ok = true;

            foreach (string name in ShipShaders)
            {
                Shader shader = Resolve(name);

                if (shader == null)
                {
                    sb.AppendLine("MISSING  " + name);
                    ok = false;
                    continue;
                }

                // Force the compiler to actually produce messages for this
                // shader rather than reporting a stale or empty cache.
                string path = AssetDatabase.GetAssetPath(shader);
                if (!string.IsNullOrEmpty(path))
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

                int count = ShaderUtil.GetShaderMessageCount(shader);
                bool hasError = ShaderUtil.ShaderHasError(shader);

                sb.AppendLine((hasError ? "ERRORS   " : "ok       ") + name +
                              "   (messages: " + count + ")   " + path);

                if (count > 0)
                {
                    ShaderMessage[] messages = ShaderUtil.GetShaderMessages(shader);
                    for (int i = 0; i < messages.Length; i++)
                    {
                        ShaderMessage m = messages[i];
                        sb.AppendLine("    [" + m.severity + "] " + m.file + ":" + m.line +
                                      "  " + m.message +
                                      (string.IsNullOrEmpty(m.messageDetails)
                                          ? ""
                                          : " | " + m.messageDetails.Trim()));
                    }
                }

                // Report the sub-shader count and enforce the structural variants this shader
                // must keep - a dropped sub-shader (the G-24 fallback) or a dropped keyword
                // (impostor hemi/full, season) fails the build rather than shipping silently.
                int subshaderCount = ShaderUtil.GetShaderData(shader).SubshaderCount;
                sb.AppendLine("         subshaders: " + subshaderCount);

                VariantRequirement req = RequirementFor(name);
                if (subshaderCount < req.minSubshaders)
                {
                    sb.AppendLine("    MISSING VARIANT: expected >= " + req.minSubshaders +
                                  " sub-shaders, found " + subshaderCount);
                    ok = false;
                }
                if (req.mustHaveKeywords != null)
                {
                    var space = shader.keywordSpace.keywordNames;
                    foreach (string kw in req.mustHaveKeywords)
                    {
                        bool present = System.Array.IndexOf(space, kw) >= 0;
                        if (!present)
                        {
                            sb.AppendLine("    MISSING VARIANT: keyword " + kw + " not declared");
                            ok = false;
                        }
                    }
                }

                if (hasError) ok = false;
            }

            report = sb.ToString();
            return ok;
        }

        /// <summary>
        /// Resolves a declared shader name to its asset. The single resolver every
        /// UC editor tool uses, so none of them can disagree about whether one of
        /// our shaders is present.
        ///
        /// Shader.Find is tried first, then a scan of our own shader folder that
        /// reads each asset's <c>Shader.name</c>. The fallback is not optional:
        /// Shader.Find does not reliably resolve <c>Hidden/</c> shaders in batch
        /// mode, so relying on it alone reports a shader as missing when it is
        /// merely hidden or not referenced by a material yet.
        /// </summary>
        public static Shader Resolve(string declaredName)
        {
            Shader found = Shader.Find(declaredName);
            if (found != null) return found;

            string[] guids = AssetDatabase.FindAssets(
                "t:Shader", new[] { "Assets/UniverseCreator/Shaders" });

            for (int i = 0; i < guids.Length; i++)
            {
                string p = AssetDatabase.GUIDToAssetPath(guids[i]);
                Shader s = AssetDatabase.LoadAssetAtPath<Shader>(p);
                if (s != null && s.name == declaredName) return s;
            }
            return null;
        }
    }
}
