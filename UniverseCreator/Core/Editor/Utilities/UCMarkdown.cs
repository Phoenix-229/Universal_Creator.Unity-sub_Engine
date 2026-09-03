// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCMarkdown.cs
//  Purpose: Render the manual's Markdown to HTML, in-engine, deterministically.
//
//  WHY A RENDERER IN THE PROJECT
//  -----------------------------
//  The manual is written and maintained as Markdown because that is what a
//  developer can diff and edit. It is READ as HTML because that is what opens
//  in a browser with a table of contents, working cross-references and a
//  favicon. Converting by hand would mean two documents to keep in step, and
//  the second one would be wrong within a week. Converting with an outside
//  tool would mean the manual could not be rebuilt from inside the project.
//
//  So the conversion is code, it lives beside the manual, and it is exact
//  about one thing above all: the anchor slugs it generates for headings match
//  the ones GitHub generates, because the manual's own 144 internal links were
//  written against those. Change the slug rule and every cross-reference in the
//  document breaks silently -- the link still exists, it just lands nowhere.
//
//  WHAT IT SUPPORTS
//  ----------------
//  Exactly the constructs the manual uses, and no more: ATX headings, fenced
//  code blocks with an optional language, pipe tables with alignment, ordered
//  and unordered lists with nesting, blockquotes, horizontal rules,
//  paragraphs, and inline code, bold, italic and links. An unsupported
//  construct is passed through as escaped text rather than guessed at, so a
//  new one shows up as itself instead of vanishing.
//
//  Everything is ordinal string work with no culture-sensitive comparison and
//  no dictionary enumeration, so the same Markdown gives byte-identical HTML on
//  any machine (C-06).
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UniverseCreator.Editor
{
    /// <summary> One heading, for building a table of contents beside the document. </summary>
    public struct UCManualHeading
    {
        public int level;
        public string text;
        public string slug;

        public UCManualHeading(int level, string text, string slug)
        {
            this.level = level; this.text = text; this.slug = slug;
        }
    }


    public static class UCMarkdown
    {
        #region "Anchors"
        /// <summary> GitHub's heading-slug rule, which the manual's cross-references were written against: lower case,
        /// every character that is not a letter, a digit, a space, a hyphen or an underscore removed, then spaces
        /// turned into hyphens. Nothing is collapsed -- "Creator - Developer" keeps its double hyphen, because that is
        /// what the link in the document says. </summary>
        public static string Slug(string heading)
        {
            if (string.IsNullOrEmpty(heading)) { return string.Empty; }

            string text = StripInline(heading);
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ' ') { sb.Append('-'); continue; }
                if (c == '-' || c == '_') { sb.Append(c); continue; }
                if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); }
            }
            return sb.ToString();
        }


        /// <summary> Heading text with its inline markup removed, so a slug is taken from the words and not from the
        /// asterisks and backticks around them -- which is also what GitHub does. </summary>
        private static string StripInline(string text)
        {
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '`' || c == '*' || c == '~') { continue; }
                if (c == '[') { continue; }
                if (c == ']' && i + 1 < text.Length && text[i + 1] == '(')
                {
                    int close = text.IndexOf(')', i);
                    if (close < 0) { break; }
                    i = close;
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
        #endregion


        #region "Escaping"
        public static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) { return string.Empty; }

            StringBuilder sb = new StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
        #endregion


        #region "Inline"
        /// <summary> Render one line's inline markup. Code spans are recognised FIRST and their contents are only
        /// escaped, never parsed, because the manual is full of code containing asterisks and underscores -- treating
        /// `a_b * c` as italics inside a code span is the classic way a generated manual mangles the very text a reader
        /// came to copy. </summary>
        public static string Inline(string text)
        {
            if (string.IsNullOrEmpty(text)) { return string.Empty; }

            StringBuilder sb = new StringBuilder(text.Length + 32);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                // A backslash escape hands the next character through as itself.
                if (c == '\\' && i + 1 < text.Length)
                {
                    sb.Append(Escape(text[i + 1].ToString()));
                    i += 2;
                    continue;
                }

                if (c == '`')
                {
                    int fence = 1;
                    while (i + fence < text.Length && text[i + fence] == '`') { fence++; }
                    string close = new string('`', fence);
                    int end = text.IndexOf(close, i + fence, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        string code = text.Substring(i + fence, end - (i + fence));
                        sb.Append("<code>").Append(Escape(code)).Append("</code>");
                        i = end + fence;
                        continue;
                    }
                }

                if (c == '[')
                {
                    int label = MatchBracket(text, i);
                    if (label > 0 && label + 1 < text.Length && text[label + 1] == '(')
                    {
                        int url = text.IndexOf(')', label + 2);
                        if (url > 0)
                        {
                            string shown = text.Substring(i + 1, label - i - 1);
                            string target = text.Substring(label + 2, url - label - 2).Trim();
                            sb.Append("<a href=\"").Append(Escape(target)).Append("\">")
                              .Append(Inline(shown)).Append("</a>");
                            i = url + 1;
                            continue;
                        }
                    }
                }

                if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        sb.Append("<strong>").Append(Inline(text.Substring(i + 2, end - i - 2)))
                          .Append("</strong>");
                        i = end + 2;
                        continue;
                    }
                }

                if (c == '*' || (c == '_' && IsWordBoundary(text, i - 1)))
                {
                    int end = text.IndexOf(c, i + 1);
                    // An emphasis run never spans a space on both sides of nothing, and never wraps an empty string:
                    // `a * b * c` is arithmetic in this manual far more often than it is italics.
                    if (end > i + 1 && text[i + 1] != ' ' && text[end - 1] != ' '
                        && (c == '*' || IsWordBoundary(text, end + 1)))
                    {
                        sb.Append("<em>").Append(Inline(text.Substring(i + 1, end - i - 1))).Append("</em>");
                        i = end + 1;
                        continue;
                    }
                }

                sb.Append(Escape(c.ToString()));
                i++;
            }
            return sb.ToString();
        }


        /// <summary> The index of the ']' that closes the '[' at <paramref name="open"/>, counting nested brackets so a
        /// label containing an array index still closes where it should. -1 when it never closes. </summary>
        private static int MatchBracket(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '[') { depth++; }
                else if (text[i] == ']')
                {
                    depth--;
                    if (depth == 0) { return i; }
                }
            }
            return -1;
        }


        /// <summary> True when the character at <paramref name="index"/> is outside a word, so `snake_case_name` keeps
        /// its underscores and only a `_word_` surrounded by space or punctuation becomes emphasis. </summary>
        private static bool IsWordBoundary(string text, int index)
        {
            if (index < 0 || index >= text.Length) { return true; }
            char c = text[index];
            return !char.IsLetterOrDigit(c) && c != '_';
        }
        #endregion


        #region "Blocks"
        /// <summary> Render a whole document's body. The outline is filled as a side effect rather than by a second
        /// pass, so the table of contents can never list a heading the body does not carry or miss one it does. </summary>
        public static string Render(string markdown, List<UCManualHeading> outline)
        {
            string[] lines = SplitLines(markdown);
            StringBuilder sb = new StringBuilder(markdown.Length * 2);
            HashSet<string> usedSlugs = new HashSet<string>();
            int i = 0;

            while (i < lines.Length)
            {
                string line = lines[i];
                string trimmed = line.TrimEnd();

                if (trimmed.Trim().Length == 0) { i++; continue; }

                if (IsFence(trimmed)) { i = Fence(lines, i, sb); continue; }
                if (IsHeading(trimmed)) { i = Heading(trimmed, i, sb, outline, usedSlugs); continue; }
                if (IsRule(trimmed)) { sb.AppendLine("<hr>"); i++; continue; }
                if (IsTableRow(trimmed) && i + 1 < lines.Length && IsTableDivider(lines[i + 1]))
                { i = Table(lines, i, sb); continue; }
                if (trimmed.TrimStart().StartsWith(">", StringComparison.Ordinal)) { i = Quote(lines, i, sb); continue; }
                if (BulletAt(trimmed) >= 0 || NumberAt(trimmed) >= 0) { i = List(lines, i, sb, 0); continue; }

                i = Paragraph(lines, i, sb);
            }

            return sb.ToString();
        }


        public static string[] SplitLines(string text)
        {
            if (text == null) { return new string[0]; }
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }


        private static bool IsFence(string line)
        {
            return line.TrimStart().StartsWith("```", StringComparison.Ordinal);
        }


        private static bool IsHeading(string line)
        {
            if (!line.StartsWith("#", StringComparison.Ordinal)) { return false; }
            int hashes = 0;
            while (hashes < line.Length && line[hashes] == '#') { hashes++; }
            return hashes >= 1 && hashes <= 6 && hashes < line.Length && line[hashes] == ' ';
        }


        /// <summary> A rule, and deliberately not a table divider: a divider always begins with a pipe in this manual,
        /// and a run of dashes on its own is the section break the author typed. </summary>
        private static bool IsRule(string line)
        {
            string t = line.Trim();
            if (t.Length < 3) { return false; }
            char c = t[0];
            if (c != '-' && c != '*' && c != '_') { return false; }
            for (int i = 0; i < t.Length; i++) { if (t[i] != c) { return false; } }
            return true;
        }


        private static bool IsTableRow(string line)
        {
            return line.TrimStart().StartsWith("|", StringComparison.Ordinal);
        }


        private static bool IsTableDivider(string line)
        {
            string t = line.Trim();
            if (!t.StartsWith("|", StringComparison.Ordinal)) { return false; }
            bool sawDash = false;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '-') { sawDash = true; continue; }
                if (c == '|' || c == ':' || c == ' ') { continue; }
                return false;
            }
            return sawDash;
        }


        private static int Fence(string[] lines, int start, StringBuilder sb)
        {
            string opener = lines[start].TrimStart();
            string language = opener.Substring(3).Trim();
            int i = start + 1;
            StringBuilder code = new StringBuilder();

            while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                code.Append(Escape(lines[i].TrimEnd())).Append('\n');
                i++;
            }
            if (i < lines.Length) { i++; }   // consume the closing fence

            sb.Append("<pre><code");
            if (language.Length > 0) { sb.Append(" class=\"lang-").Append(Escape(language)).Append('"'); }
            sb.Append('>').Append(code.ToString()).AppendLine("</code></pre>");
            return i;
        }


        private static int Heading(string line, int index, StringBuilder sb, List<UCManualHeading> outline,
            HashSet<string> usedSlugs)
        {
            int level = 0;
            while (level < line.Length && line[level] == '#') { level++; }
            string text = line.Substring(level).Trim();

            // A repeated heading gets "-1", "-2" ... appended, which is again GitHub's rule, so a document with two
            // "Fields" sections still has two reachable anchors instead of one that wins.
            string slug = Slug(text);
            string unique = slug;
            int suffix = 1;
            while (usedSlugs.Contains(unique))
            {
                unique = slug + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }
            usedSlugs.Add(unique);

            string tag = "h" + level.ToString(CultureInfo.InvariantCulture);
            sb.Append('<').Append(tag).Append(" id=\"").Append(Escape(unique)).Append("\">")
              .Append(Inline(text)).Append("</").Append(tag).AppendLine(">");

            if (outline != null) { outline.Add(new UCManualHeading(level, StripInline(text).Trim(), unique)); }
            return index + 1;
        }


        /// <summary> Render one pipe table. The column count comes from the DIVIDER row and not from the header,
        /// because a divider never has an empty cell while a header can, and a short header would then silently
        /// drop a column from every row beneath it. Short rows are padded instead of ragged, so the table a browser
        /// lays out has the rectangle the Markdown described. </summary>
        private static int Table(string[] lines, int start, StringBuilder sb)
        {
            string[] header = Cells(lines[start]);
            string[] align = Alignments(lines[start + 1]);
            int columns = Math.Max(header.Length, align.Length);
            int i = start + 2;

            sb.AppendLine("<table>");
            sb.Append("<thead><tr>");
            for (int c = 0; c < columns; c++)
            {
                Cell(sb, "th", c < header.Length ? header[c] : string.Empty, align, c);
            }
            sb.AppendLine("</tr></thead>");
            sb.AppendLine("<tbody>");

            while (i < lines.Length && IsTableRow(lines[i].TrimEnd()))
            {
                string[] row = Cells(lines[i]);
                sb.Append("<tr>");
                for (int c = 0; c < columns; c++)
                {
                    Cell(sb, "td", c < row.Length ? row[c] : string.Empty, align, c);
                }
                sb.AppendLine("</tr>");
                i++;
            }

            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");
            return i;
        }
        /// <summary> Split one table row into its cells. A pipe INSIDE a code span is content, not a boundary -- this
        /// manual's tables are largely tables of code, and `a|b` in a cell is the sort of thing that silently shifts
        /// every column to its right when the splitter is a plain Split('|'). A backslash-escaped pipe is content too.
        /// The empty cell a trailing pipe leaves behind is dropped, which is the `| a | b |` shape the author wrote. </summary>
        private static string[] Cells(string line)
        {
            string t = line.Trim();
            List<string> cells = new List<string>();
            StringBuilder cell = new StringBuilder();
            bool inCode = false;
            int i = t.StartsWith("|", StringComparison.Ordinal) ? 1 : 0;

            for (; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '\\' && i + 1 < t.Length) { cell.Append(c).Append(t[i + 1]); i++; continue; }
                if (c == '`') { inCode = !inCode; cell.Append(c); continue; }
                if (c == '|' && !inCode)
                {
                    cells.Add(cell.ToString().Trim());
                    cell.Length = 0;
                    continue;
                }
                cell.Append(c);
            }
            if (cell.ToString().Trim().Length > 0) { cells.Add(cell.ToString().Trim()); }
            return cells.ToArray();
        }


        /// <summary> Per-column alignment read off the divider: `:--` left, `--:` right, `:-:` centre, `---` unstated.
        /// Unstated is left EMPTY rather than defaulted to left, so the stylesheet keeps the say over columns the
        /// author did not align -- numeric columns in this manual are right-aligned by the sheet. </summary>
        private static string[] Alignments(string divider)
        {
            string[] parts = Cells(divider);
            string[] align = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                bool left = p.StartsWith(":", StringComparison.Ordinal);
                bool right = p.EndsWith(":", StringComparison.Ordinal);
                if (left && right) { align[i] = "center"; }
                else if (right) { align[i] = "right"; }
                else if (left) { align[i] = "left"; }
                else { align[i] = string.Empty; }
            }
            return align;
        }
        private static void Cell(StringBuilder sb, string tag, string text, string[] align, int column)
        {
            sb.Append('<').Append(tag);
            if (column < align.Length && align[column].Length > 0)
            {
                sb.Append(" style=\"text-align:").Append(align[column]).Append('"');
            }
            sb.Append('>').Append(Inline(text)).Append("</").Append(tag).Append('>');
        }


        /// <summary> The character index of a bullet marker, or -1. A marker is a dash, star or plus FOLLOWED BY A
        /// SPACE: without that test every `--` comment line and every `*pointer` in the prose would open a list. </summary>
        private static int BulletAt(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) { i++; }
            if (i >= line.Length) { return -1; }
            char c = line[i];
            if ((c == '-' || c == '*' || c == '+') && i + 1 < line.Length && line[i + 1] == ' ') { return i; }
            return -1;
        }


        /// <summary> The character index of an ordered marker (`1. ` or `1) `), or -1. </summary>
        private static int NumberAt(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) { i++; }
            int digits = i;
            while (digits < line.Length && char.IsDigit(line[digits])) { digits++; }
            if (digits == i) { return -1; }
            if (digits < line.Length && (line[digits] == '.' || line[digits] == ')')
                && digits + 1 < line.Length && line[digits + 1] == ' ') { return i; }
            return -1;
        }


        /// <summary> The column a line's content starts in, with a tab counted as four so a tab-indented sub-item
        /// nests under a space-indented parent instead of beside it. </summary>
        private static int Indent(string line)
        {
            int n = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == ' ') { n++; continue; }
                if (line[i] == '\t') { n += 4; continue; }
                break;
            }
            return n;
        }
        /// <summary> The indent column of a list marker, or -1 when the line is not a list item. </summary>
        private static int MarkerIndent(string line)
        {
            if (BulletAt(line) < 0 && NumberAt(line) < 0) { return -1; }
            return Indent(line);
        }


        /// <summary> One list item's text, with its marker removed. </summary>
        private static string ItemText(string line)
        {
            int bullet = BulletAt(line);
            if (bullet >= 0) { return line.Substring(bullet + 1).Trim(); }

            int number = NumberAt(line);
            if (number < 0) { return line.Trim(); }
            int i = number;
            while (i < line.Length && char.IsDigit(line[i])) { i++; }
            if (i < line.Length) { i++; }        // the '.' or ')'
            return line.Substring(i).Trim();
        }


        private static int NextContent(string[] lines, int from)
        {
            for (int i = from; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length > 0) { return i; }
            }
            return -1;
        }


        /// <summary> Render one list and everything nested inside it.
        ///
        /// Indentation decides nesting, and the child list is opened INSIDE the parent's still-open &lt;li&gt; --
        /// a nested &lt;ul&gt; that is a sibling of the item it belongs to is the mistake browsers render as a
        /// second top-level list, which is how a numbered procedure loses its sub-steps.
        ///
        /// A line that is not an item but is indented under one is that item's continuation, so a paragraph or a
        /// fenced example written under a step stays part of the step. A blank line ends the list only when what
        /// follows is neither a further item nor an indented continuation. </summary>
        private static int List(string[] lines, int start, StringBuilder sb, int minIndent)
        {
            int baseIndent = MarkerIndent(lines[start]);
            bool ordered = NumberAt(lines[start]) >= 0;
            sb.AppendLine(ordered ? "<ol>" : "<ul>");

            int i = start;
            bool open = false;
            while (i < lines.Length)
            {
                string line = lines[i].TrimEnd();

                if (line.Trim().Length == 0)
                {
                    int next = NextContent(lines, i + 1);
                    if (next < 0) { break; }
                    bool nextIsItem = MarkerIndent(lines[next]) >= baseIndent;
                    bool nextIsContinuation = Indent(lines[next]) >= baseIndent + 2;
                    if (!nextIsItem && !nextIsContinuation) { break; }
                    i++;
                    continue;
                }

                int marker = MarkerIndent(line);
                if (marker >= 0)
                {
                    if (marker < baseIndent || marker < minIndent) { break; }
                    if (marker > baseIndent)
                    {
                        if (!open) { sb.Append("<li>"); open = true; }
                        i = List(lines, i, sb, baseIndent + 1);
                        continue;
                    }

                    if (open) { sb.AppendLine("</li>"); }
                    sb.Append("<li>").Append(Inline(ItemText(line)));
                    open = true;
                    i++;
                    continue;
                }

                if (!open || Indent(line) < baseIndent + 2) { break; }

                if (IsFence(line)) { i = Fence(lines, i, sb); continue; }
                sb.Append(' ').Append(Inline(line.Trim()));
                i++;
            }
            if (open) { sb.AppendLine("</li>"); }
            sb.AppendLine(ordered ? "</ol>" : "</ul>");
            return i;
        }
        /// <summary> Render one blockquote. Its contents go back through <see cref="Render"/> rather than through the
        /// inline pass, because the manual's notes hold bulleted lists and short code blocks, and an inline-only
        /// quote would flatten those into one run of text. The outline is passed as null: a heading inside a note is
        /// not a section of the document and must not appear in the table of contents. </summary>
        private static int Quote(string[] lines, int start, StringBuilder sb)
        {
            StringBuilder inner = new StringBuilder();
            int i = start;

            while (i < lines.Length)
            {
                string line = lines[i].TrimEnd();
                string t = line.TrimStart();

                if (t.StartsWith(">", StringComparison.Ordinal))
                {
                    string body = t.Substring(1);
                    if (body.StartsWith(" ", StringComparison.Ordinal)) { body = body.Substring(1); }
                    inner.Append(body).Append('\n');
                    i++;
                    continue;
                }

                if (line.Trim().Length == 0) { break; }

                // A lazy continuation: a wrapped quote line the author did not re-prefix. Markdown keeps it in the
                // quote, and dropping it here would start a stray paragraph mid-note.
                inner.Append(t).Append('\n');
                i++;
            }

            sb.AppendLine("<blockquote>");
            sb.Append(Render(inner.ToString(), null));
            sb.AppendLine("</blockquote>");
            return i;
        }


        /// <summary> Render one paragraph, ending it at a blank line or at any block that interrupts it. The
        /// interrupting tests are skipped on the FIRST line, which is the line the caller already decided was a
        /// paragraph -- testing it again would loop forever on a line no other rule claims. </summary>
        private static int Paragraph(string[] lines, int start, StringBuilder sb)
        {
            StringBuilder text = new StringBuilder();
            int i = start;

            while (i < lines.Length)
            {
                string line = lines[i].TrimEnd();
                if (line.Trim().Length == 0) { break; }

                if (i > start && (IsHeading(line) || IsFence(line) || IsRule(line) || IsTableRow(line)
                    || line.TrimStart().StartsWith(">", StringComparison.Ordinal)
                    || MarkerIndent(line) >= 0)) { break; }

                if (text.Length > 0) { text.Append('\n'); }
                text.Append(line.Trim());
                i++;
            }

            sb.Append("<p>").Append(Inline(text.ToString())).AppendLine("</p>");
            return i;
        }
        #endregion
    }
}
