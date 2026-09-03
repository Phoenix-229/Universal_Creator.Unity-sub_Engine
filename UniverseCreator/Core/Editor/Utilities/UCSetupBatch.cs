// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCSetupBatch.cs
//  Purpose: Run the two authoring-asset steps in ONE editor session.
//
//  WHY THIS EXISTS
//  ---------------
//  Batchmode takes a single -executeMethod, and every launch pays a full domain
//  reload and asset import before the first line of the job runs. Splitting the
//  banner and building the manual are both "make the shipped documents from
//  their sources" steps, they are always wanted together, and running them as
//  two launches costs two of those startups for no reason.
//
//  Both steps run even when the first fails. A manual is readable without its
//  masthead, and stopping at the first problem hides the second one for another
//  whole cycle -- which is exactly how a verification pass turns into four.
// -----------------------------------------------------------------------------

using System.Text;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor
{
    public static class UCSetupBatch
    {
        private const string Prefix = "UCSETUP";


        [MenuItem("Tools/Universe Creator/Setup/Split Banner and Build Manual")]
        public static void RunFromMenu()
        {
            string report;
            bool ok = Run(out report);
            if (ok) { Debug.Log(report); } else { Debug.LogError(report); }
        }


        public static void RunBatch()
        {
            string report;
            bool ok = Run(out report);
            Debug.Log(report);
            EditorApplication.Exit(ok ? 0 : 1);
        }


        public static bool Run(out string report)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Prefix + " BEGIN");

            string brand;
            bool brandOk = UCBrandImages.Run(out brand);
            sb.AppendLine(brand);

            string manual;
            bool manualOk = UCManualBuilder.Run(out manual);
            sb.AppendLine(manual);

            sb.AppendLine(Prefix + " result " + ((brandOk && manualOk) ? "PASS" : "FAIL"));
            sb.Append(Prefix + " END");
            report = sb.ToString();
            return brandOk && manualOk;
        }
    }
}
