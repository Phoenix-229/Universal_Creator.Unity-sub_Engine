// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCManualLauncher.cs
//  Purpose: Open the reader's HTML manual off disk, from a menu item or a button.
//
//  The manual a developer reads is the HTML that UCManualBuilder renders from the
//  authored Markdown. This opens that HTML with the OS default handler (a browser)
//  via a file:// URL, because the page references its masthead and favicon by a
//  path RELATIVE to itself -- AssetDatabase.OpenAsset would hand it to a text
//  editor and the relative hrefs would resolve to nothing.
//
//  If the HTML is not on disk yet -- a fresh checkout that has never run the
//  builder -- this renders it first rather than dead-ending on a missing file,
//  so the button and the menu item are always live.
// -----------------------------------------------------------------------------

using System.IO;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor
{
    /// <summary>
    /// Single entry point that both the Window menu item and the creator inspector
    /// button call, so there is one definition of "where the manual is" and "what
    /// opening it means".
    /// </summary>
    public static class UCManualLauncher
    {
        /// <summary>
        /// Open the HTML manual in the OS default handler. Builds it first if it is
        /// not on disk. Returns false only if the build was attempted and failed.
        /// </summary>
        public static bool Open()
        {
            string manual = Path.Combine(
                UCManualBuilder.ProjectRoot(),
                UCManualBuilder.OutputAssetPath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(manual))
            {
                string report;
                bool built = UCManualBuilder.Run(out report);
                if (!built || !File.Exists(manual))
                {
                    Debug.LogError("Universe Creator: could not open the manual -- build failed.\n" + report);
                    return false;
                }
                Debug.Log(report);
            }

            Application.OpenURL("file:///" + manual.Replace('\\', '/'));
            return true;
        }
    }
}
