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
            "Hidden/UniverseCreator/WaterRipple"
        };

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
                // Shader.Find does not reliably resolve Hidden/ shaders in batch
                // mode, and a shader that fails to resolve would otherwise be
                // reported as MISSING when it is merely not referenced yet.
                // Loading by asset path is unambiguous.
                Shader shader = Shader.Find(name);
                if (shader == null) shader = FindByPath(name);

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

                if (hasError) ok = false;
            }

            report = sb.ToString();
            return ok;
        }

        /// <summary>
        /// Resolves a declared shader name by scanning our own shader folder and
        /// reading each asset's <c>Shader.name</c>. Used when Shader.Find returns
        /// null, which happens for Hidden/ shaders in batch mode.
        /// </summary>
        private static Shader FindByPath(string declaredName)
        {
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
