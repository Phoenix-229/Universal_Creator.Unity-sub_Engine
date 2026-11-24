using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>Reruns the registered content/import, unit, execution-domain and climate checks affected by the natural-content repair.</summary>
        /// <remarks>Main-thread isolated batch entry point. Runs the original assertions without skips and reports exceptions as failures.
        /// Exits zero only when every check passes; rendered appearance, Player builds and device limits remain separate acceptance.</remarks>
        public static void RunContentRepairBatch()
        {
            checks = 0; failures.Clear();
            var groups = new (string id, System.Action run)[] {
                ("Content imports", TestMobileTextureBudget), ("Content bindings/export", TestSavedMaterialTextureBindings),
                ("UC-T01-08", TestCompiledExecutionDomains), ("UC-T03-03", TestGroundNodeContracts),
                ("UC-T08-03/04/05", TestClimateBaselineContracts) };
            foreach (var group in groups)
                try { group.run(); } catch (System.Exception error) { Check(false, group.id + " repair acceptance exception: " + error); }
            System.Console.WriteLine(Report(failures.Count == 0)); UnityEditor.EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }

        // UC-T02-06/10: exercise the installed native material serialization layout under the real headless gate.
        private static void TestSavedMaterialTextureBindings()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Check(shader != null, "UC-T02-06 material binding fixture resolves the installed URP shader.");
            if (shader == null) return;
            var material = new Material(shader);
            var color = new Texture2D(2, 2);
            var normal = new Texture2D(2, 2);
            try
            {
                material.SetTexture("_BaseMap", color);
                material.SetTexture("_BumpMap", normal);
                var bindings = UCTextureBudget.ReadSavedTextureBindings(material);
                Check(bindings.TryGetValue("_BaseMap", out Texture savedColor) && savedColor == color &&
                      bindings.TryGetValue("_BumpMap", out Texture savedNormal) && savedNormal == normal,
                    "UC-T02-06 headless material auditing preserves separate authored color and normal bindings.");
                material.SetTexture("_BaseMap", normal);
                material.SetTexture("_BumpMap", null);
                var edited = UCTextureBudget.ReadSavedTextureBindings(material);
                Check(edited["_BaseMap"] == normal && edited["_BumpMap"] == null && bindings["_BaseMap"] == color,
                    "UC-T02-06 material binding edits and removal appear in fresh reads without changing earlier results.");
                Check(material.shader == shader && color != null && normal != null,
                    "UC-T02-06 binding audit leaves borrowed materials and textures alive and unchanged.");
                var roles = new Dictionary<string, string> { { "Plant_Color.png", "_ExtraMap" }, { "Plant_Mask.png", "_ColorMap" } };
                Check(UCTextureBudget.RoleFor("Plant_Color.png", null, roles, out _) == UCTextureBudget.Role.Data &&
                    UCTextureBudget.RoleFor("Plant_Mask.png", null, roles, out _) == UCTextureBudget.Role.Color,
                    "UC content audit: observed ShaderGraph packed-mask/albedo bindings override misleading filenames without assuming channel packing.");
                var packedMask = new UCTextureBudget.Entry { AndroidOverridden = true, AndroidBefore = 512, Cap = 512, PackedMaskSrgbWrong = true };
                Check(!UCTextureBudget.IsCompliant(packedMask), "UC content audit: a correctly capped packed mask still fails when gamma sampling changes its data channels.");
                Check(UCContentDistribution.IsGameContent("Assets\\UniverseCreator\\Content\\Trees\\Plant.prefab") &&
                    UCContentDistribution.IsGameContent("Assets/UniverseCreator/Content.meta") &&
                    !UCContentDistribution.IsGameContent("Assets/UniverseCreator/ContentTools/Tool.cs"),
                    "UC content distribution: game assets and folder metadata are excluded without excluding similarly named toolkit code.");
                string[] toolkit = UCContentDistribution.ToolkitAssets();
                Check(toolkit.Length > 0 && System.Array.Exists(toolkit, path => path.EndsWith("/UC_TerrainLit.shader")) &&
                    !System.Array.Exists(toolkit, UCContentDistribution.IsGameContent),
                    "UC content distribution: the actual explicit export inventory retains the UC shader and contains no game-only content dependencies.");
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(color);
                Object.DestroyImmediate(normal);
            }
        }
    }
}
