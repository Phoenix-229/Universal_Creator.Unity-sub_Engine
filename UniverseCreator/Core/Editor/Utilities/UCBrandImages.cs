// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCBrandImages.cs
//  Purpose: Split the one authored banner into the per-system brand images that
//           the two manuals, the two parent inspectors and the scene icons use.
//
//  WHY THIS IS CODE AND NOT FOUR FILES CHECKED IN BY HAND
//  -----------------------------------------------------
//  The banner is ONE authored artwork holding both products side by side. Four
//  hand-cut PNGs would drift from it the first time it is re-exported, and
//  nothing would say which crop was taken. This derives all four from the
//  source, refuses to guess when the source is not the size it expects, and
//  writes through AssetDatabase so UNITY mints every .meta GUID -- a GUID typed
//  by hand is a GUID that can collide with a real one.
//
//  WHAT IT PRODUCES, AND WHO READS IT
//  ----------------------------------
//    Universe Creator half  -> Core/Editor/Resources/Branding/UniverseCreator*
//    Road & City Master half-> ../RoadCityMaster/ManualImages/RoadCityMaster*
//
//  Each half is written twice, because the two jobs want different pixels:
//    <Name>.png       the full half, for a manual's masthead and the strip
//                     across the top of that system's parent inspector.
//    <Name>_Icon.png  the same half resampled to 128x128, for the manual's
//                     favicon and for the scene/hierarchy icon that replaces
//                     Unity's grey cuboid on an object the system created.
//  The halves are already all but square (512x506), so the icon is a resample
//  and not a crop -- nothing of the artwork is thrown away to make it.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UniverseCreator.Editor
{
    public static class UCBrandImages
    {
        private const string Prefix = "UCBRAND";

        /// <summary> The authored banner, beside Assets/ in the project folder. Deliberately NOT inside Assets: it is
        /// the source art, and importing a 1024-wide texture the tool never draws would cost build size for nothing. </summary>
        public const string SourceFileName = "image.png";

        /// <summary> What the source has to be for the split to mean anything. A banner exported at another size is not
        /// silently re-divided -- the halves would cut through the artwork and no one would see it until the manual
        /// was opened. </summary>
        public const int ExpectedWidth = 1024;
        public const int ExpectedHeight = 506;

        /// <summary> The icon edge. 128 is Unity's own script-icon size, so the hierarchy row and the scene billboard
        /// both get their pixels without resampling a second time. </summary>
        public const int IconEdge = 128;

        public const string UniverseCreatorBannerPath =
            "Assets/UniverseCreator/Core/Editor/Resources/Branding/UniverseCreator.png";
        public const string UniverseCreatorIconPath =
            "Assets/UniverseCreator/Core/Editor/Resources/Branding/UniverseCreator_Icon.png";

        /// <summary> Loadable with <c>Resources.Load&lt;Texture2D&gt;</c> from editor code. The folder is inside an
        /// Editor folder, so it is editor-only and costs a player build nothing. </summary>
        public const string UniverseCreatorResourceName = "Branding/UniverseCreator";
        public const string UniverseCreatorIconResourceName = "Branding/UniverseCreator_Icon";

        public const string RoadCityMasterBannerPath =
            "Assets/RoadCityMaster/ManualImages/RoadCityMaster.png";
        public const string RoadCityMasterIconPath =
            "Assets/RoadCityMaster/ManualImages/RoadCityMaster_Icon.png";


        [MenuItem("Tools/Universe Creator/Setup/Split Brand Banner")]
        public static void RunFromMenu()
        {
            string report;
            Run(out report);
            Debug.Log(report);
        }


        public static void RunBatch()
        {
            string report;
            bool ok = Run(out report);
            Debug.Log(report);
            EditorApplication.Exit(ok ? 0 : 1);
        }


        /// <summary> The project folder, one level above Assets. </summary>
        public static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }


        public static string SourcePath()
        {
            return Path.Combine(ProjectRoot(), SourceFileName);
        }


        public static bool Run(out string report)
        {
            List<string> problems = new List<string>();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Prefix + " BEGIN");

            string source = SourcePath();
            sb.AppendLine(Prefix + " source " + source);

            if (!File.Exists(source))
            {
                problems.Add("the banner was not found at " + source
                    + ". Put the authored artwork there under the name " + SourceFileName + ".");
                return Finish(sb, problems, out report);
            }

            Texture2D banner = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                if (!ImageConversion.LoadImage(banner, File.ReadAllBytes(source), false))
                {
                    problems.Add("the file at " + source + " is not a PNG this editor can decode.");
                    return Finish(sb, problems, out report);
                }

                sb.AppendLine(Prefix + " read   " + banner.width.ToString(CultureInfo.InvariantCulture) + " x "
                    + banner.height.ToString(CultureInfo.InvariantCulture));

                if (banner.width != ExpectedWidth || banner.height != ExpectedHeight)
                {
                    problems.Add("the banner is " + banner.width.ToString(CultureInfo.InvariantCulture) + " x "
                        + banner.height.ToString(CultureInfo.InvariantCulture) + ", and the split is written for "
                        + ExpectedWidth.ToString(CultureInfo.InvariantCulture) + " x "
                        + ExpectedHeight.ToString(CultureInfo.InvariantCulture)
                        + ". Re-export at that size, or change the constants and say why.");
                    return Finish(sb, problems, out report);
                }

                int half = banner.width / 2;
                Write(banner, 0, half, "Universe Creator", UniverseCreatorBannerPath, UniverseCreatorIconPath,
                    sb, problems);
                Write(banner, half, banner.width - half, "Road & City Master", RoadCityMasterBannerPath,
                    RoadCityMasterIconPath, sb, problems);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(banner);
            }

            AssetDatabase.Refresh();
            return Finish(sb, problems, out report);
        }


        /// <summary> Cut one half out of the banner and write it twice: at full size, and resampled to the icon edge.
        ///
        /// The pixels are copied with <c>GetPixels</c> rather than blitted through a RenderTexture. A blit would go
        /// through the active graphics device and its result depends on the pipeline's colour space, which is exactly
        /// the kind of dependency that makes a generated asset differ between two machines; a pixel copy is arithmetic
        /// and gives the same PNG every time (C-06). </summary>
        private static void Write(Texture2D banner, int x, int width, string label, string bannerPath, string iconPath,
            StringBuilder sb, List<string> problems)
        {
            Color32[] pixels = banner.GetPixels32();
            Color32[] cut = new Color32[width * banner.height];
            for (int row = 0; row < banner.height; row++)
            {
                Array.Copy(pixels, row * banner.width + x, cut, row * width, width);
            }

            Texture2D full = new Texture2D(width, banner.height, TextureFormat.RGBA32, false, false);
            Texture2D icon = null;
            try
            {
                full.SetPixels32(cut);
                full.Apply(false, false);

                if (!Save(full, bannerPath, sb, problems)) { return; }

                icon = Resample(full, IconEdge, IconEdge);
                Save(icon, iconPath, sb, problems);

                sb.AppendLine(Prefix + " split  " + label + "  x " + x.ToString(CultureInfo.InvariantCulture)
                    + "  width " + width.ToString(CultureInfo.InvariantCulture));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(full);
                if (icon != null) { UnityEngine.Object.DestroyImmediate(icon); }
            }
        }


        /// <summary> Box-filter down to the icon size: every destination pixel is the mean of the source pixels it
        /// covers. A point sample would drop three quarters of a 512 px banner's detail and the lettering would break
        /// up; a box filter over the exact coverage rectangle keeps it legible and needs no filtering hardware. </summary>
        private static Texture2D Resample(Texture2D source, int width, int height)
        {
            Color32[] src = source.GetPixels32();
            Color32[] dst = new Color32[width * height];

            for (int j = 0; j < height; j++)
            {
                int y0 = (int)((long)j * source.height / height);
                int y1 = (int)((long)(j + 1) * source.height / height);
                if (y1 <= y0) { y1 = y0 + 1; }

                for (int i = 0; i < width; i++)
                {
                    int x0 = (int)((long)i * source.width / width);
                    int x1 = (int)((long)(i + 1) * source.width / width);
                    if (x1 <= x0) { x1 = x0 + 1; }

                    long r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int rowStart = y * source.width;
                        for (int xx = x0; xx < x1; xx++)
                        {
                            Color32 c = src[rowStart + xx];
                            r += c.r; g += c.g; b += c.b; a += c.a; n++;
                        }
                    }

                    dst[j * width + i] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                }
            }

            Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            result.SetPixels32(dst);
            result.Apply(false, false);
            return result;
        }


        /// <summary> Write the PNG, import it, and set the importer to what an editor GUI texture needs. The importer
        /// is set explicitly rather than left at the project default because the default compresses -- and a
        /// compressed masthead shows blocking artefacts on the lettering at the size it is drawn. </summary>
        private static bool Save(Texture2D texture, string assetPath, StringBuilder sb, List<string> problems)
        {
            string full = Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
            string folder = Path.GetDirectoryName(full);

            try
            {
                if (!Directory.Exists(folder)) { Directory.CreateDirectory(folder); }
                File.WriteAllBytes(full, ImageConversion.EncodeToPNG(texture));
            }
            catch (Exception e)
            {
                problems.Add("could not write " + assetPath + ": " + e.Message);
                return false;
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                problems.Add("Unity imported " + assetPath + " but not as a texture, so its importer cannot be set.");
                return false;
            }

            importer.textureType = TextureImporterType.GUI;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = Mathf.Max(512, Mathf.NextPowerOfTwo(Mathf.Max(texture.width, texture.height)));
            importer.SaveAndReimport();

            sb.AppendLine(Prefix + " wrote  " + assetPath + "  "
                + texture.width.ToString(CultureInfo.InvariantCulture) + " x "
                + texture.height.ToString(CultureInfo.InvariantCulture));
            return true;
        }


        private static bool Finish(StringBuilder sb, List<string> problems, out string report)
        {
            for (int i = 0; i < problems.Count; i++) { sb.AppendLine(Prefix + " FAIL   " + problems[i]); }
            sb.AppendLine(Prefix + " result " + (problems.Count == 0 ? "PASS" : "FAIL"));
            sb.Append(Prefix + " END");
            report = sb.ToString();
            return problems.Count == 0;
        }
    }
}
