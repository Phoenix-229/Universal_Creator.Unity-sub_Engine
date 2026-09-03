// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCManualBuilder.cs
//  Purpose: Turn the authored Markdown manual into the HTML a developer reads.
//
//  WHAT THIS IS FOR
//  ----------------
//  UniverseCreator_Documentation.md is the manual. It is written in Markdown
//  because that is what can be diffed, reviewed and edited a paragraph at a
//  time. It is READ as HTML because 7,793 lines want a sidebar you can jump
//  from, a filter you can type into, cross-references that actually travel, and
//  a favicon so the tab is identifiable among twenty others.
//
//  This is the one direction of that conversion and it is re-runnable: edit the
//  Markdown, run the menu item, and the HTML is the Markdown again. There is no
//  second copy of the prose to keep in step, which is the failure this avoids.
//
//  WHAT IT WRITES, AND WHY THE PATHS LOOK LIKE THAT
//  ------------------------------------------------
//  Output sits beside the Markdown, at Assets/UniverseCreator/, so the manual
//  travels with the system it documents. The masthead and the favicon are
//  referenced by RELATIVE path into Core/Editor/Resources/Branding/, because a
//  developer opens this file off disk with file:// and an absolute path or a
//  project-relative "Assets/..." href would resolve to nothing there.
//
//  NOTHING DATED IS WRITTEN INTO IT
//  --------------------------------
//  No build stamp, no generated-on line. Two runs over the same Markdown give
//  byte-identical HTML (C-06), which is what makes the output reviewable in a
//  diff instead of showing a changed timestamp on every line of a 7,793-line
//  document. The Markdown is the provenance.
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
    public static class UCManualBuilder
    {
        private const string Prefix = "UCMANUAL";
        /// <summary> The authored manual. Project-relative, because that is what AssetDatabase understands. </summary>
        public const string SourceAssetPath = "Assets/UniverseCreator/UniverseCreator_Documentation.md";

        /// <summary> Where the reader's copy goes: beside the Markdown, so the two never get separated. </summary>
        public const string OutputAssetPath = "Assets/UniverseCreator/UniverseCreator_Manual.html";

        /// <summary> Brand images, relative to the OUTPUT file rather than to the project, so the href works when the
        /// page is opened straight off disk. <see cref="UCBrandImages"/> writes both. </summary>
        public const string MastheadHref = "Core/Editor/Resources/Branding/UniverseCreator.png";
        public const string FaviconHref = "Core/Editor/Resources/Branding/UniverseCreator_Icon.png";

        /// <summary> Which heading levels reach the sidebar. The document carries 48 H2 and 230 H3 sections, which is a
        /// usable index; adding its 167 H4 sub-sections would make the sidebar longer than most of the sections it
        /// points at, so H4 is reachable from the body and from the document's own contents table instead. </summary>
        public const int SidebarDeepestLevel = 3;


        [MenuItem("Tools/Universe Creator/Setup/Build Developer Manual")]
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


        public static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }


        private static string FullPath(string assetPath)
        {
            return Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        }
        /// <summary> Read the Markdown, render it, and write the page. The report names the counts a reader can check
        /// against the source -- headings in, headings indexed, bytes out -- because "it wrote something" is not the
        /// same as "it wrote the whole document", and a truncated manual looks fine until you reach the end of it.
        /// </summary>
        public static bool Run(out string report)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Prefix + " BEGIN");

            string source = FullPath(SourceAssetPath);
            sb.AppendLine(Prefix + " source " + SourceAssetPath);

            if (!File.Exists(source))
            {
                sb.AppendLine(Prefix + " FAIL   the manual was not found at " + source);
                sb.AppendLine(Prefix + " result FAIL");
                sb.Append(Prefix + " END");
                report = sb.ToString();
                return false;
            }

            string markdown = File.ReadAllText(source);
            string[] sourceLines = UCMarkdown.SplitLines(markdown);

            List<UCManualHeading> outline = new List<UCManualHeading>();
            string body = UCMarkdown.Render(markdown, outline);
            string title = Title(outline);
            string page = Page(title, body, outline);

            string output = FullPath(OutputAssetPath);
            try
            {
                File.WriteAllText(output, page, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                sb.AppendLine(Prefix + " FAIL   could not write " + OutputAssetPath + ": " + e.Message);
                sb.AppendLine(Prefix + " result FAIL");
                sb.Append(Prefix + " END");
                report = sb.ToString();
                return false;
            }

            AssetDatabase.ImportAsset(OutputAssetPath, ImportAssetOptions.ForceUpdate);
            report = Describe(sb, sourceLines.Length, outline, page.Length);
            return true;
        }
        private static string Describe(StringBuilder sb, int sourceLineCount, List<UCManualHeading> outline,
            int outputLength)
        {
            int sidebar = 0;
            for (int i = 0; i < outline.Count; i++)
            {
                if (outline[i].level >= 2 && outline[i].level <= SidebarDeepestLevel) { sidebar++; }
            }

            sb.AppendLine(Prefix + " read   " + sourceLineCount.ToString(CultureInfo.InvariantCulture)
                + " lines of Markdown");
            sb.AppendLine(Prefix + " index  " + outline.Count.ToString(CultureInfo.InvariantCulture)
                + " headings, " + sidebar.ToString(CultureInfo.InvariantCulture) + " in the sidebar");
            sb.AppendLine(Prefix + " wrote  " + OutputAssetPath + "  "
                + outputLength.ToString(CultureInfo.InvariantCulture) + " characters");
            sb.AppendLine(Prefix + " result PASS");
            sb.Append(Prefix + " END");
            return sb.ToString();
        }


        /// <summary> The page title, taken from the document's own H1 so the browser tab says what the author called
        /// the manual rather than what this file happens to name it. </summary>
        private static string Title(List<UCManualHeading> outline)
        {
            for (int i = 0; i < outline.Count; i++)
            {
                if (outline[i].level == 1) { return outline[i].text; }
            }
            return "Universe Creator - Developer Manual";
        }
        private static string Page(string title, string body, List<UCManualHeading> outline)
        {
            StringBuilder sb = new StringBuilder(body.Length + 8192);
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\">");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>").Append(UCMarkdown.Escape(title)).AppendLine("</title>");
            sb.Append("<link rel=\"icon\" type=\"image/png\" href=\"").Append(FaviconHref).AppendLine("\">");
            sb.AppendLine("<style>");
            sb.AppendLine(Styles());
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div id=\"layout\">");
            sb.Append(Contents(title, outline));
            sb.AppendLine("<main id=\"doc\">");
            sb.Append("<img id=\"masthead\" src=\"").Append(MastheadHref).Append("\" alt=\"")
              .Append(UCMarkdown.Escape(title)).AppendLine("\">");
            sb.Append(body);
            sb.AppendLine("</main>");
            sb.AppendLine("</div>");
            sb.AppendLine("<script>");
            sb.AppendLine(Script());
            sb.AppendLine("</script>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }


        private static string Contents(string title, List<UCManualHeading> outline)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<nav id=\"toc\">");
            sb.Append("<div id=\"tocTitle\">").Append(UCMarkdown.Escape(title)).AppendLine("</div>");
            sb.AppendLine("<input id=\"filter\" type=\"search\" placeholder=\"Filter sections\" autocomplete=\"off\">");
            sb.AppendLine("<ul id=\"tocList\">");
            for (int i = 0; i < outline.Count; i++)
            {
                UCManualHeading heading = outline[i];
                if (heading.level < 2 || heading.level > SidebarDeepestLevel) { continue; }
                sb.Append("<li class=\"lvl").Append(heading.level.ToString(CultureInfo.InvariantCulture))
                  .Append("\"><a href=\"#").Append(UCMarkdown.Escape(heading.slug)).Append("\">")
                  .Append(UCMarkdown.Escape(heading.text)).AppendLine("</a></li>");
            }
            sb.AppendLine("</ul>");
            sb.AppendLine("</nav>");
            return sb.ToString();
        }
        /// <summary> The stylesheet, inlined rather than written as a second file. A manual that is one file can be
        /// copied, mailed and opened anywhere; a manual that needs a stylesheet beside it arrives unstyled the first
        /// time someone sends only the page. It is deliberately plain: a fixed sidebar, a measured reading column, and
        /// tables and code blocks that survive the widest rows this document has. </summary>
        private static string Styles()
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.AppendLine(@":root{--ink:#1b1f24;--dim:#5b6570;--rule:#dde2e7;--bg:#ffffff;--panel:#f6f8fa;");
            sb.AppendLine(@"      --code:#f2f4f7;--link:#0b5cad;--accent:#7a4fd0;}");
            sb.AppendLine(@"*{box-sizing:border-box;}");
            sb.AppendLine(@"html{scroll-behavior:smooth;}");
            sb.AppendLine(@"body{margin:0;background:var(--bg);color:var(--ink);");
            sb.AppendLine(@"     font:15px/1.65 -apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;}");
            sb.AppendLine(@"#layout{display:flex;align-items:flex-start;}");
            sb.AppendLine(@"#toc{position:sticky;top:0;flex:0 0 320px;height:100vh;overflow-y:auto;");
            sb.AppendLine(@"     background:var(--panel);border-right:1px solid var(--rule);padding:18px 14px 40px;}");
            sb.AppendLine(@"#tocTitle{font-weight:700;font-size:14px;letter-spacing:.01em;margin:0 0 12px;}");
            sb.AppendLine(@"#filter{width:100%;padding:7px 9px;margin:0 0 14px;border:1px solid var(--rule);");
            sb.AppendLine(@"        border-radius:6px;background:var(--bg);color:var(--ink);font-size:13px;}");
            sb.AppendLine(@"#tocList{list-style:none;margin:0;padding:0;}");
            sb.AppendLine(@"#tocList li{margin:1px 0;}");
            sb.AppendLine(@"#tocList a{display:block;padding:3px 8px;border-radius:5px;color:var(--dim);");
            sb.AppendLine(@"          text-decoration:none;font-size:13px;}");
            sb.AppendLine(@"#tocList a:hover{background:#e9edf2;color:var(--ink);}");
            sb.AppendLine(@"#tocList li.lvl2>a{color:var(--ink);font-weight:600;margin-top:9px;}");
            sb.AppendLine(@"#tocList li.lvl3>a{padding-left:20px;}");
            sb.AppendLine(@"#tocList a[data-here]{background:#e6dff8;color:var(--ink);font-weight:600;}");
            sb.AppendLine(@"#doc{flex:1 1 auto;min-width:0;max-width:1000px;padding:0 44px 140px;}");
            sb.AppendLine(@"#masthead{display:block;width:100%;max-width:640px;height:auto;margin:26px 0 8px;");
            sb.AppendLine(@"          border:1px solid var(--rule);border-radius:10px;}");
            sb.AppendLine(@"h1,h2,h3,h4,h5,h6{line-height:1.28;scroll-margin-top:14px;}");
            sb.AppendLine(@"h1{font-size:31px;margin:18px 0 6px;}");
            sb.AppendLine(@"h2{font-size:24px;margin:44px 0 10px;padding-bottom:6px;border-bottom:2px solid var(--rule);}");
            sb.AppendLine(@"h3{font-size:19px;margin:30px 0 8px;}");
            sb.AppendLine(@"h4{font-size:16px;margin:22px 0 6px;color:#2c3540;}");
            sb.AppendLine(@"h5,h6{font-size:14px;margin:18px 0 6px;color:var(--dim);}");
            sb.AppendLine(@"p{margin:11px 0;}");
            sb.AppendLine(@"a{color:var(--link);}");
            sb.AppendLine(@"hr{border:0;border-top:1px solid var(--rule);margin:34px 0;}");
            sb.AppendLine(@"code{background:var(--code);border:1px solid var(--rule);border-radius:4px;");
            sb.AppendLine(@"     padding:1px 4px;font-family:'Cascadia Mono',Consolas,'Liberation Mono',monospace;");
            sb.AppendLine(@"     font-size:13px;}");
            sb.AppendLine(@"pre{background:var(--code);border:1px solid var(--rule);border-radius:8px;");
            sb.AppendLine(@"    padding:13px 15px;overflow-x:auto;margin:14px 0;}");
            sb.AppendLine(@"pre code{background:none;border:0;padding:0;font-size:12.5px;line-height:1.55;}");
            sb.AppendLine(@"blockquote{margin:14px 0;padding:2px 16px;border-left:4px solid var(--accent);");
            sb.AppendLine(@"           background:#faf8ff;color:#38323f;}");
            sb.AppendLine(@"blockquote p{margin:8px 0;}");
            sb.AppendLine(@"ul,ol{margin:11px 0;padding-left:26px;}");
            sb.AppendLine(@"li{margin:4px 0;}");
            sb.AppendLine(@"li>ul,li>ol{margin:4px 0;}");
            sb.AppendLine(@"table{border-collapse:collapse;margin:16px 0;width:100%;display:block;overflow-x:auto;}");
            sb.AppendLine(@"th,td{border:1px solid var(--rule);padding:7px 10px;text-align:left;");
            sb.AppendLine(@"      vertical-align:top;font-size:13.5px;}");
            sb.AppendLine(@"th{background:var(--panel);font-weight:650;white-space:nowrap;}");
            sb.AppendLine(@"tbody tr:nth-child(even){background:#fbfcfd;}");
            sb.AppendLine(@"img{max-width:100%;height:auto;}");
            sb.AppendLine(@"@media (max-width:1080px){");
            sb.AppendLine(@"  #layout{flex-direction:column;}");
            sb.AppendLine(@"  #toc{position:static;height:auto;max-height:none;width:100%;flex:1 1 auto;");
            sb.AppendLine(@"       border-right:0;border-bottom:1px solid var(--rule);}");
            sb.AppendLine(@"  #doc{padding:0 20px 90px;}}");
            sb.AppendLine(@"@media print{");
            sb.AppendLine(@"  #toc,#filter{display:none;}");
            sb.AppendLine(@"  #doc{max-width:none;padding:0;}");
            sb.AppendLine(@"  pre,table,blockquote{break-inside:avoid;}}");
            return sb.ToString();
        }
        /// <summary> Two conveniences, both of which a 7,793-line manual needs and neither of which needs a library:
        /// a filter over the sidebar, and a mark on the section you are currently reading. Everything degrades to a
        /// plain scrolling document if the script does not run, because the links are real anchors and the sidebar is
        /// real markup -- nothing here is required to read the manual. </summary>
        private static string Script()
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.AppendLine(@"(function(){");
            sb.AppendLine(@"  var filter = document.getElementById('filter');");
            sb.AppendLine(@"  var list = document.getElementById('tocList');");
            sb.AppendLine(@"  if (filter && list) {");
            sb.AppendLine(@"    var items = list.getElementsByTagName('li');");
            sb.AppendLine(@"    filter.addEventListener('input', function(){");
            sb.AppendLine(@"      var needle = filter.value.trim().toLowerCase();");
            sb.AppendLine(@"      for (var i = 0; i < items.length; i++) {");
            sb.AppendLine(@"        var text = items[i].textContent.toLowerCase();");
            sb.AppendLine(@"        items[i].style.display =");
            sb.AppendLine(@"          (needle === '' || text.indexOf(needle) >= 0) ? '' : 'none';");
            sb.AppendLine(@"      }");
            sb.AppendLine(@"    });");
            sb.AppendLine(@"  }");
            sb.AppendLine(@"  var links = {};");
            sb.AppendLine(@"  if (list) {");
            sb.AppendLine(@"    var anchors = list.getElementsByTagName('a');");
            sb.AppendLine(@"    for (var a = 0; a < anchors.length; a++) {");
            sb.AppendLine(@"      var href = anchors[a].getAttribute('href');");
            sb.AppendLine(@"      if (href && href.charAt(0) === '#') { links[href.substring(1)] = anchors[a]; }");
            sb.AppendLine(@"    }");
            sb.AppendLine(@"  }");
            sb.AppendLine(@"  var current = null;");
            sb.AppendLine(@"  function mark(id) {");
            sb.AppendLine(@"    var link = links[id];");
            sb.AppendLine(@"    if (!link || link === current) { return; }");
            sb.AppendLine(@"    if (current) { current.removeAttribute('data-here'); }");
            sb.AppendLine(@"    link.setAttribute('data-here', '1');");
            sb.AppendLine(@"    current = link;");
            sb.AppendLine(@"  }");
            sb.AppendLine(@"  if (window.IntersectionObserver) {");
            sb.AppendLine(@"    var seen = new IntersectionObserver(function(entries){");
            sb.AppendLine(@"      for (var e = 0; e < entries.length; e++) {");
            sb.AppendLine(@"        if (entries[e].isIntersecting) { mark(entries[e].target.id); }");
            sb.AppendLine(@"      }");
            sb.AppendLine(@"    }, {rootMargin: '0px 0px -80% 0px'});");
            sb.AppendLine(@"    var heads = document.querySelectorAll('#doc h2[id], #doc h3[id]');");
            sb.AppendLine(@"    for (var h = 0; h < heads.length; h++) { seen.observe(heads[h]); }");
            sb.AppendLine(@"  }");
            sb.AppendLine(@"})();");
            return sb.ToString();
        }
    }
}
