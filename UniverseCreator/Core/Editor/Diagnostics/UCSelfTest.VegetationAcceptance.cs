// UC-T06-14: one focused gate runs the registered production vegetation contracts; rendered/device evidence stays separate.
using System;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        /// <summary>Runs Topic 6 production placement, authoring and ownership fixtures, reports exact counts and exits Unity with zero on success or one on failure.</summary>
        /// <remarks>Invoke through Unity's executeMethod after freezing an isolated UC source snapshot. This main-thread batch gate does not certify rendered quality or sustained phone performance.</remarks>
        public static void RunVegetationBatch()
        {
            checks = 0; failures.Clear();
            var groups = new (string id, Action run)[] {
                ("UC-T06 graph binding", TestVegetationGraphBinding),
                ("UC-T08-15 inspector consumers", TestVegetationInspectorGroups),
                ("UC-T06-01", TestVegetationOwnershipInventory), ("UC-T06-02", TestVegetationMigration),
                ("UC-T06-03", TestVegetationRegionMasks), ("UC-T06-04", TestVegetationHabitat),
                ("UC-T06-05", TestVegetationCandidates), ("UC-T06-06", TestVegetationIdentity),
                ("UC-T06-07", TestVegetationGroundContact), ("UC-T06-08", TestVegetationVariationAndCollision),
                ("UC-T06-09", TestVegetationAppearance), ("UC-T06-10", TestVegetationSpatialBatching),
                ("UC-T06-11", TestVegetationAquaticHabitat), ("UC-T06-12", TestVegetationOwnershipLifetime),
                ("UC-T06-13", TestVegetationPrototypeContent), ("UC-T06-15/16", TestVegetationCategoryAuthoring),
                ("UC-T06-17", TestVegetationDensityDiagnosis), ("UC-T06-18", TestVegetationManualIntent) };
            foreach (var group in groups)
                try { group.run(); } catch (Exception error) { Check(false, group.id + " vegetation acceptance exception: " + error); }
            bool ok = failures.Count == 0;
            Debug.Log(Report(ok)); Console.WriteLine(Report(ok));
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
