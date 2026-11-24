using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Konbini.Conversion
{
    // Format-neutral document: every reader produces this, every writer consumes it.
    // Keeps structure (headings, paragraphs, lists, tables, code); inline styling is simplified to plain text.
    public class Doc
    {
        public enum Kind { Heading, Paragraph, ListItem, Code, Table }
        public class Block
        {
            public Kind Kind; public int Level; public string Text; public List<List<string>> Rows;
            public Block(Kind k, string text, int level = 0) { Kind = k; Text = text; Level = level; }
        }
        public readonly List<Block> Blocks = new List<Block>();
        public string Title;

        public void H(string t, int level) { if (!string.IsNullOrWhiteSpace(t)) Blocks.Add(new Block(Kind.Heading, t.Trim(), Math.Max(1, Math.Min(6, level)))); }
        public void P(string t) { if (!string.IsNullOrWhiteSpace(t)) Blocks.Add(new Block(Kind.Paragraph, t.Trim())); }
        public void Li(string t, int level = 0) { if (!string.IsNullOrWhiteSpace(t)) Blocks.Add(new Block(Kind.ListItem, t.Trim(), level)); }
        public void Code(string t) { Blocks.Add(new Block(Kind.Code, t)); }
        public void Table(List<List<string>> rows) { if (rows.Count > 0) Blocks.Add(new Block(Kind.Table, null) { Rows = rows }); }
    }

    public static class DocReaders
    {
        public static string ReadText(string path)
        {
            using (var r = new StreamReader(path, Encoding.UTF8, true)) return r.ReadToEnd();
        }

        public static Doc Read(string path, string id)
        {
            switch (id)
            {
                case "txt": return FromText(ReadText(path));
                case "md": return FromMarkdown(ReadText(path));
                case "html": return FromHtml(ReadText(path));
                case "rtf": return FromText(RtfToText(ReadText(path)));
                case "docx": return FromDocx(path);
                case "odt": return FromOdt(path);
                case "pptx": return FromPptx(path);
                case "epub": return FromEpub(path);
                case "srt": case "vtt": return FromText(string.Join("\n\n", Subtitles.Read(path, id).Select(c => c.Text)));
                default: throw new NotSupportedException("Can't read ." + id + " as a document.");
            }
        }

        public static Doc FromText(string text)
        {
            var d = new Doc();
            foreach (var para in Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n")) d.P(para);
            return d;
        }

        public static Doc FromMarkdown(string md)
        {
            var d = new Doc();
            var lines = md.Replace("\r\n", "\n").Split('\n');
            var para = new StringBuilder();
            Action flush = () => { if (para.Length > 0) { d.P(Inline(para.ToString())); para.Clear(); } };
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i], t = l.Trim();
                Match m;
                if (t.StartsWith("```"))
                {
                    flush();
                    var code = new StringBuilder();
                    for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```"); i++) code.AppendLine(lines[i]);
                    d.Code(code.ToString().TrimEnd());
                }
                else if ((m = Regex.Match(t, @"^(#{1,6})\s+(.*?)\s*#*$")).Success) { flush(); d.H(Inline(m.Groups[2].Value), m.Groups[1].Length); }
                else if (t.StartsWith("|") && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1].Trim(), @"^\|?\s*:?-{2,}"))
                {
                    flush();
                    var rows = new List<List<string>> { SplitRow(t) };
                    for (i += 2; i < lines.Length && lines[i].Trim().StartsWith("|"); i++) rows.Add(SplitRow(lines[i].Trim()));
                    i--;
                    d.Table(rows);
                }
                else if ((m = Regex.Match(l, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$")).Success) { flush(); d.Li(Inline(m.Groups[3].Value), m.Groups[1].Value.Length / 2); }
                else if (t.Length == 0) flush();
                else if (Regex.IsMatch(t, @"^(-{3,}|\*{3,}|_{3,})$")) flush();
                else para.Append(para.Length > 0 ? " " : "").Append(t.TrimStart('>', ' '));
            }
            flush();
            return d;
        }

        private static List<string> SplitRow(string row)
        {
            return row.Trim().Trim('|').Split('|').Select(c => Inline(c.Trim())).ToList();
        }

        private static string Inline(string s)
        {
            s = Regex.Replace(s, @"!\[([^\]]*)\]\([^)]*\)", "$1");
            s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)]+)\)", "$1 ($2)");
            s = Regex.Replace(s, @"(\*\*|__)(.+?)\1", "$2");
            s = Regex.Replace(s, @"(\*|_)(.+?)\1", "$2");
            s = Regex.Replace(s, @"`([^`]+)`", "$1");
            return s;
        }

        public static Doc FromHtml(string html)
        {
            var d = new Doc();
            var t = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (t.Success) d.Title = Clean(t.Groups[1].Value);
            html = Regex.Replace(html, @"<(script|style|head|noscript|svg)\b.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
            // Tokenise into block-level elements; everything else is inline text.
            var re = new Regex(@"<(h[1-6]|p|li|pre|tr|td|th|table|div|br|blockquote|section|article|ul|ol|hr)\b[^>]*>|</(h[1-6]|p|li|pre|tr|table|div|blockquote|section|article|ul|ol)>",
                               RegexOptions.IgnoreCase);
            var buf = new StringBuilder();
            string current = "p"; int listDepth = 0;
            List<List<string>> table = null; List<string> row = null;
            Action flush = () =>
            {
                string text = Clean(buf.ToString()); buf.Clear();
                if (text.Length == 0) return;
                if (row != null) { row.Add(text); return; }
                if (current.Length == 2 && current[0] == 'h' && char.IsDigit(current[1])) d.H(text, current[1] - '0');
                else if (current == "li") d.Li(text, Math.Max(0, listDepth - 1));
                else if (current == "pre") d.Code(text);
                else d.P(text);
            };
            int pos = 0;
            foreach (Match m in re.Matches(html))
            {
                buf.Append(html, pos, m.Index - pos); pos = m.Index + m.Length;
                string open = m.Groups[1].Value.ToLowerInvariant(), close = m.Groups[2].Value.ToLowerInvariant();
                if (open == "br") { buf.Append('\n'); continue; }
                if (open == "td" || open == "th") { if (row != null) { flush(); } continue; }
                flush();
                if (open == "table") { table = new List<List<string>>(); }
                else if (open == "tr") { if (table == null) table = new List<List<string>>(); row = new List<string>(); }
                else if (close == "tr") { if (row != null && table != null && row.Count > 0) table.Add(row); row = null; }
                else if (close == "table") { if (row != null && table != null && row.Count > 0) table.Add(row); row = null; if (table != null) d.Table(table); table = null; }
                else if (open == "ul" || open == "ol") listDepth++;
                else if (close == "ul" || close == "ol") listDepth = Math.Max(0, listDepth - 1);
                else if (open.Length > 0) current = open;
                else current = "p";
            }
            buf.Append(html.Substring(pos));
            flush();
            if (table != null) d.Table(table);
            return d;
        }

        private static string Clean(string fragment)
        {
            string s = Regex.Replace(fragment, @"<[^>]+>", "");
            s = WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"[ \t\r\f\v]+", " ");
            s = Regex.Replace(s, @" *\n *", "\n");
            return s.Trim();
        }

        // Strips RTF control words, keeping text, paragraph breaks and \'hh / \uN characters.
        public static string RtfToText(string rtf)
        {
            var sb = new StringBuilder();
            var skipStack = new Stack<bool>();
            bool skip = false;
            int ucSkip = 1;
            for (int i = 0; i < rtf.Length; i++)
            {
                char c = rtf[i];
                if (c == '{') { skipStack.Push(skip); continue; }
                if (c == '}') { skip = skipStack.Count > 0 && skipStack.Pop(); continue; }
                if (c == '\\')
                {
                    if (i + 1 >= rtf.Length) break;
                    char n = rtf[i + 1];
                    if (n == '\\' || n == '{' || n == '}') { if (!skip) sb.Append(n); i++; continue; }
                    if (n == '\'' && i + 3 < rtf.Length)
                    {
                        if (!skip) sb.Append(Encoding.GetEncoding(1252).GetString(new[] { Convert.ToByte(rtf.Substring(i + 2, 2), 16) }));
                        i += 3; continue;
                    }
                    if (n == '*') { skip = true; i++; continue; }
                    var m = Regex.Match(rtf.Substring(i + 1, Math.Min(40, rtf.Length - i - 1)), @"^([a-zA-Z]+)(-?\d+)? ?");
                    if (!m.Success) { i++; continue; }
                    string word = m.Groups[1].Value; string arg = m.Groups[2].Value;
                    i += m.Length;
                    switch (word)
                    {
                        case "par": case "line": case "row": if (!skip) sb.Append('\n'); break;
                        case "tab": case "cell": if (!skip) sb.Append('\t'); break;
                        case "uc": ucSkip = arg.Length > 0 ? int.Parse(arg) : 1; break;
                        case "u":
                            if (!skip && arg.Length > 0) { int v = int.Parse(arg); if (v < 0) v += 65536; sb.Append((char)v); }
                            i += ucSkip; break;
                        case "fonttbl": case "colortbl": case "stylesheet": case "info": case "pict": case "header": case "footer":
                        case "listtable": case "listoverridetable": case "rsidtbl": case "generator": case "themedata": case "latentstyles":
                            skip = true; break;
                    }
                    continue;
                }
                if (c == '\r' || c == '\n') continue;
                if (!skip) sb.Append(c);
            }
            return sb.ToString();
        }

        private static XDocument ZipXml(ZipArchive z, string name)
        {
            var e = z.GetEntry(name);
            if (e == null) return null;
            using (var s = e.Open()) return XDocument.Load(s);
        }

        public static Doc FromDocx(string path)
        {
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var d = new Doc();
            using (var z = ZipFile.OpenRead(path))
            {
                var x = ZipXml(z, "word/document.xml");
                var body = x.Root.Element(w + "body");
                foreach (var el in body.Elements())
                {
                    if (el.Name == w + "p") AddDocxParagraph(d, el, w);
                    else if (el.Name == w + "tbl")
                        d.Table(el.Elements(w + "tr").Select(tr => tr.Elements(w + "tc")
                            .Select(tc => string.Join("\n", tc.Elements(w + "p").Select(p => DocxText(p, w)))).ToList()).ToList());
                }
            }
            return d;
        }

        private static void AddDocxParagraph(Doc d, XElement p, XNamespace w)
        {
            string text = DocxText(p, w);
            var pPr = p.Element(w + "pPr");
            string style = pPr == null || pPr.Element(w + "pStyle") == null ? "" : (string)pPr.Element(w + "pStyle").Attribute(w + "val") ?? "";
            var hm = Regex.Match(style, @"^(?:Heading|heading)\s?(\d)$");
            if (style == "Title") d.H(text, 1);
            else if (hm.Success) d.H(text, int.Parse(hm.Groups[1].Value));
            else if (style.StartsWith("List"))
            {
                var ind = pPr.Element(w + "ind");
                int left = ind == null ? 720 : (int?)ind.Attribute(w + "left") ?? 720;
                d.Li(text.TrimStart('•', '·', '-', ' ', '\t'), Math.Max(0, (left - 720) / 360));
            }
            else if (style == "Code" || style == "HTMLPreformatted") d.Code(text);
            else if (pPr != null && pPr.Element(w + "numPr") != null)
            {
                var ilvl = pPr.Element(w + "numPr").Element(w + "ilvl");
                d.Li(text, ilvl == null ? 0 : (int)ilvl.Attribute(w + "val"));
            }
            else d.P(text);
        }

        private static string DocxText(XElement p, XNamespace w)
        {
            var sb = new StringBuilder();
            foreach (var n in p.Descendants())
            {
                if (n.Name == w + "t") sb.Append(n.Value);
                else if (n.Name == w + "tab") sb.Append('\t');
                else if (n.Name == w + "br" || n.Name == w + "cr") sb.Append('\n');
            }
            return sb.ToString();
        }

        public static Doc FromOdt(string path)
        {
            XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
            XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
            XNamespace table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
            var d = new Doc();
            using (var z = ZipFile.OpenRead(path))
            {
                var x = ZipXml(z, "content.xml");
                var body = x.Root.Element(office + "body").Element(office + "text");
                Action<XElement, int> walk = null;
                walk = (parent, depth) =>
                {
                    foreach (var el in parent.Elements())
                    {
                        if (el.Name == text + "h") d.H(OdtText(el, text), (int?)el.Attribute(text + "outline-level") ?? 1);
                        else if (el.Name == text + "p") { if (depth > 0) d.Li(OdtText(el, text), depth - 1); else d.P(OdtText(el, text)); }
                        else if (el.Name == text + "list") foreach (var li in el.Elements(text + "list-item")) walk(li, depth + 1);
                        else if (el.Name == table + "table")
                            d.Table(el.Descendants(table + "table-row").Select(r => r.Elements(table + "table-cell")
                                .Select(c => string.Join("\n", c.Elements(text + "p").Select(p => OdtText(p, text)))).ToList()).ToList());
                        else if (el.Name == text + "section") walk(el, depth);
                    }
                };
                walk(body, 0);
            }
            return d;
        }

        private static string OdtText(XElement el, XNamespace text)
        {
            var sb = new StringBuilder();
            foreach (var n in el.Nodes())
            {
                var t = n as XText; if (t != null) { sb.Append(t.Value); continue; }
                var e = n as XElement; if (e == null) continue;
                if (e.Name == text + "s") sb.Append(' ', (int?)e.Attribute(text + "c") ?? 1);
                else if (e.Name == text + "tab") sb.Append('\t');
                else if (e.Name == text + "line-break") sb.Append('\n');
                else sb.Append(OdtText(e, text));
            }
            return sb.ToString();
        }

        public static Doc FromPptx(string path)
        {
            XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            var d = new Doc();
            using (var z = ZipFile.OpenRead(path))
            {
                var slides = z.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"))
                    .OrderBy(e => int.Parse(Regex.Match(e.FullName, @"\d+").Value)).ToList();
                int n = 1;
                foreach (var s in slides)
                {
                    d.H("Slide " + n++, 2);
                    XDocument x; using (var st = s.Open()) x = XDocument.Load(st);
                    foreach (var p in x.Descendants(a + "p"))
                        d.P(string.Concat(p.Descendants(a + "t").Select(t => t.Value)));
                }
            }
            return d;
        }

        public static Doc FromEpub(string path)
        {
            var d = new Doc();
            using (var z = ZipFile.OpenRead(path))
            {
                XNamespace c = "urn:oasis:names:tc:opendocument:xmlns:container";
                string opfPath = (string)ZipXml(z, "META-INF/container.xml").Descendants(c + "rootfile").First().Attribute("full-path");
                var opf = ZipXml(z, opfPath);
                XNamespace o = opf.Root.Name.Namespace;
                string baseDir = opfPath.Contains("/") ? opfPath.Substring(0, opfPath.LastIndexOf('/') + 1) : "";
                var manifest = opf.Descendants(o + "item").ToDictionary(i => (string)i.Attribute("id"), i => (string)i.Attribute("href"));
                foreach (var item in opf.Descendants(o + "itemref"))
                {
                    string href;
                    if (!manifest.TryGetValue((string)item.Attribute("idref"), out href)) continue;
                    var e = z.GetEntry(baseDir + Uri.UnescapeDataString(href));
                    if (e == null) continue;
                    string html; using (var r = new StreamReader(e.Open())) html = r.ReadToEnd();
                    d.Blocks.AddRange(FromHtml(html).Blocks);
                }
            }
            return d;
        }
    }

    public static class DocWriters
    {
        public static void Write(Doc d, string path, string id)
        {
            switch (id)
            {
                case "txt": File.WriteAllText(path, ToText(d), new UTF8Encoding(false)); break;
                case "md": File.WriteAllText(path, ToMarkdown(d), new UTF8Encoding(false)); break;
                case "html": File.WriteAllText(path, ToHtml(d), new UTF8Encoding(false)); break;
                case "rtf": File.WriteAllText(path, ToRtf(d), Encoding.ASCII); break;
                case "docx": ToDocx(d, path); break;
                case "pdf": ToPdf(d, path); break;
                default: throw new NotSupportedException("Can't write documents as ." + id);
            }
        }

        public static string ToText(Doc d)
        {
            var sb = new StringBuilder();
            foreach (var b in d.Blocks)
            {
                switch (b.Kind)
                {
                    case Doc.Kind.Heading: sb.AppendLine(b.Text); sb.AppendLine(new string(b.Level == 1 ? '=' : '-', Math.Min(80, b.Text.Length))); break;
                    case Doc.Kind.ListItem: sb.Append(' ', b.Level * 2).Append("• ").AppendLine(b.Text); continue;
                    case Doc.Kind.Table: foreach (var r in b.Rows) sb.AppendLine(string.Join("\t", r)); break;
                    default: sb.AppendLine(b.Text); break;
                }
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        public static string ToMarkdown(Doc d)
        {
            var sb = new StringBuilder();
            Doc.Kind? prev = null;
            foreach (var b in d.Blocks)
            {
                if (prev.HasValue && !(prev == Doc.Kind.ListItem && b.Kind == Doc.Kind.ListItem)) sb.AppendLine();
                switch (b.Kind)
                {
                    case Doc.Kind.Heading: sb.Append('#', b.Level).Append(' ').AppendLine(b.Text); break;
                    case Doc.Kind.ListItem: sb.Append(' ', b.Level * 2).Append("- ").AppendLine(b.Text); break;
                    case Doc.Kind.Code: sb.AppendLine("```").AppendLine(b.Text).AppendLine("```"); break;
                    case Doc.Kind.Table:
                        int cols = b.Rows.Max(r => r.Count);
                        for (int i = 0; i < b.Rows.Count; i++)
                        {
                            var cells = Enumerable.Range(0, cols).Select(c => c < b.Rows[i].Count ? b.Rows[i][c].Replace("|", "\\|").Replace("\n", " ") : "");
                            sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
                            if (i == 0) sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", cols))).AppendLine();
                        }
                        break;
                    default: sb.AppendLine(b.Text); break;
                }
                prev = b.Kind;
            }
            return sb.ToString();
        }

        private static string E(string s) { return WebUtility.HtmlEncode(s ?? "").Replace("\n", "<br>"); }

        public static string ToHtml(Doc d)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>" + E(d.Title ?? "Document") + "</title>");
            sb.AppendLine("<style>body{font-family:Segoe UI,system-ui,sans-serif;max-width:820px;margin:40px auto;padding:0 16px;line-height:1.55;color:#1d1f24}" +
                          "table{border-collapse:collapse;margin:12px 0}td,th{border:1px solid #d5d8de;padding:6px 10px;text-align:left}th{background:#f3f4f6}" +
                          "pre{background:#f3f4f6;padding:12px;border-radius:6px;overflow:auto}</style>\n</head>\n<body>");
            bool inList = false;
            foreach (var b in d.Blocks)
            {
                if (inList && b.Kind != Doc.Kind.ListItem) { sb.AppendLine("</ul>"); inList = false; }
                switch (b.Kind)
                {
                    case Doc.Kind.Heading: sb.AppendLine("<h" + b.Level + ">" + E(b.Text) + "</h" + b.Level + ">"); break;
                    case Doc.Kind.ListItem:
                        if (!inList) { sb.AppendLine("<ul>"); inList = true; }
                        sb.AppendLine("<li" + (b.Level > 0 ? " style=\"margin-left:" + (b.Level * 20) + "px\"" : "") + ">" + E(b.Text) + "</li>"); break;
                    case Doc.Kind.Code: sb.AppendLine("<pre>" + WebUtility.HtmlEncode(b.Text) + "</pre>"); break;
                    case Doc.Kind.Table:
                        sb.AppendLine("<table>");
                        for (int i = 0; i < b.Rows.Count; i++)
                        {
                            string tag = i == 0 ? "th" : "td";
                            sb.Append("<tr>");
                            foreach (var c in b.Rows[i]) sb.Append("<" + tag + ">" + E(c) + "</" + tag + ">");
                            sb.AppendLine("</tr>");
                        }
                        sb.AppendLine("</table>"); break;
                    default: sb.AppendLine("<p>" + E(b.Text) + "</p>"); break;
                }
            }
            if (inList) sb.AppendLine("</ul>");
            sb.AppendLine("</body>\n</html>");
            return sb.ToString();
        }

        private static string R(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (c == '\\' || c == '{' || c == '}') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\line ");
                else if (c == '\t') sb.Append("\\tab ");
                else if (c > 127) sb.Append("\\u").Append((short)c).Append('?');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string ToRtf(Doc d)
        {
            var sb = new StringBuilder("{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Calibri;}{\\f1 Consolas;}}\\fs22\n");
            foreach (var b in d.Blocks)
            {
                switch (b.Kind)
                {
                    case Doc.Kind.Heading: sb.Append("{\\pard\\sb200\\sa100\\b\\fs" + (b.Level == 1 ? 36 : b.Level == 2 ? 30 : 26) + " " + R(b.Text) + "\\par}\n"); break;
                    case Doc.Kind.ListItem: sb.Append("{\\pard\\li" + (360 + b.Level * 360) + "\\fi-200 \\bullet  " + R(b.Text) + "\\par}\n"); break;
                    case Doc.Kind.Code: sb.Append("{\\pard\\f1\\fs18 " + R(b.Text) + "\\par}\n"); break;
                    case Doc.Kind.Table:
                        foreach (var row in b.Rows)
                        {
                            sb.Append("\\trowd");
                            for (int c = 0; c < row.Count; c++) sb.Append("\\cellx" + ((c + 1) * 9000 / Math.Max(1, row.Count)));
                            foreach (var cell in row) sb.Append("\\pard\\intbl " + R(cell) + "\\cell ");
                            sb.Append("\\row\n");
                        }
                        sb.Append("\\pard\\par\n"); break;
                    default: sb.Append("{\\pard\\sa160 " + R(b.Text) + "\\par}\n"); break;
                }
            }
            return sb.Append('}').ToString();
        }

        private static string X(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") if (c == '\t' || c == '\n' || c == '\r' || c >= 0x20) sb.Append(c);
            return System.Security.SecurityElement.Escape(sb.ToString());
        }

        private static string Run(string text)
        {
            // Splits on newlines/tabs into Word break/tab elements.
            var sb = new StringBuilder("<w:r>");
            var parts = Regex.Split(text ?? "", @"(\n|\t)");
            foreach (var p in parts)
            {
                if (p == "\n") sb.Append("<w:br/>");
                else if (p == "\t") sb.Append("<w:tab/>");
                else if (p.Length > 0) sb.Append("<w:t xml:space=\"preserve\">").Append(X(p)).Append("</w:t>");
            }
            return sb.Append("</w:r>").ToString();
        }

        public static void ToDocx(Doc d, string path)
        {
            var body = new StringBuilder();
            foreach (var b in d.Blocks)
            {
                switch (b.Kind)
                {
                    case Doc.Kind.Heading: body.Append("<w:p><w:pPr><w:pStyle w:val=\"Heading" + Math.Min(3, b.Level) + "\"/></w:pPr>" + Run(b.Text) + "</w:p>"); break;
                    case Doc.Kind.ListItem: body.Append("<w:p><w:pPr><w:pStyle w:val=\"ListBullet\"/><w:ind w:left=\"" + (720 + b.Level * 360) + "\" w:hanging=\"360\"/></w:pPr><w:r><w:t xml:space=\"preserve\">• </w:t></w:r>" + Run(b.Text) + "</w:p>"); break;
                    case Doc.Kind.Code: body.Append("<w:p><w:pPr><w:pStyle w:val=\"Code\"/></w:pPr>" + Run(b.Text) + "</w:p>"); break;
                    case Doc.Kind.Table:
                        body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/>" +
                                    "<w:tblBorders><w:top w:val=\"single\" w:sz=\"4\"/><w:left w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/>" +
                                    "<w:right w:val=\"single\" w:sz=\"4\"/><w:insideH w:val=\"single\" w:sz=\"4\"/><w:insideV w:val=\"single\" w:sz=\"4\"/></w:tblBorders></w:tblPr>");
                        foreach (var row in b.Rows)
                        {
                            body.Append("<w:tr>");
                            foreach (var cell in row) body.Append("<w:tc><w:p>" + Run(cell) + "</w:p></w:tc>");
                            body.Append("</w:tr>");
                        }
                        body.Append("</w:tbl><w:p/>"); break;
                    default: body.Append("<w:p>" + Run(b.Text) + "</w:p>"); break;
                }
            }
            const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
            string styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles " + W + ">" +
                "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\"/><w:sz w:val=\"22\"/></w:rPr></w:rPrDefault>" +
                "<w:pPrDefault><w:pPr><w:spacing w:after=\"160\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
                "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:before=\"360\"/><w:outlineLvl w:val=\"0\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"36\"/></w:rPr></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:before=\"240\"/><w:outlineLvl w:val=\"1\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"30\"/></w:rPr></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Heading3\"><w:name w:val=\"heading 3\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:outlineLvl w:val=\"2\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"26\"/></w:rPr></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"ListBullet\"><w:name w:val=\"List Bullet\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"60\"/></w:pPr></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Code\"><w:name w:val=\"Code\"/><w:basedOn w:val=\"Normal\"/><w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\"/><w:sz w:val=\"19\"/></w:rPr></w:style>" +
                "</w:styles>";
            string document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document " + W + "><w:body>" + body +
                "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";
            OpenXml.WritePackage(path, new Dictionary<string, string>
            {
                { "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                    "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>" },
                { "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>" },
                { "word/_rels/document.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>" },
                { "word/document.xml", document },
                { "word/styles.xml", styles },
            });
        }

        public static void ToPdf(Doc d, string path)
        {
            var pdf = new PdfWriter();
            foreach (var b in d.Blocks)
            {
                switch (b.Kind)
                {
                    case Doc.Kind.Heading: pdf.Paragraph(b.Text, b.Level == 1 ? PdfWriter.Style.H1 : b.Level == 2 ? PdfWriter.Style.H2 : PdfWriter.Style.H3, 0, 4); break;
                    case Doc.Kind.ListItem: pdf.Paragraph("•  " + b.Text, PdfWriter.Style.Body, 14 + b.Level * 14, 2); break;
                    case Doc.Kind.Code: pdf.Paragraph(b.Text, PdfWriter.Style.Mono, 8, 8); break;
                    case Doc.Kind.Table: pdf.Table(b.Rows); break;
                    default: pdf.Paragraph(b.Text); break;
                }
            }
            pdf.Save(path);
        }
    }

    public static class OpenXml
    {
        public static void WritePackage(string path, Dictionary<string, string> parts)
        {
            if (File.Exists(path)) File.Delete(path);
            using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var kv in parts)
                {
                    var e = z.CreateEntry(kv.Key, CompressionLevel.Optimal);
                    using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(kv.Value);
                }
        }
    }

    public class Cue { public string Start, End, Text; }

    public static class Subtitles
    {
        public static List<Cue> Read(string path, string id)
        {
            var cues = new List<Cue>();
            string text = DocReaders.ReadText(path).Replace("\r\n", "\n");
            foreach (var block in Regex.Split(text, @"\n\s*\n"))
            {
                var lines = block.Split('\n').Where(l => l.Trim().Length > 0).ToList();
                int ti = lines.FindIndex(l => l.Contains("-->"));
                if (ti < 0) continue;
                var m = Regex.Match(lines[ti], @"([\d:.,]+)\s*-->\s*([\d:.,]+)");
                if (!m.Success) continue;
                cues.Add(new Cue { Start = Norm(m.Groups[1].Value), End = Norm(m.Groups[2].Value), Text = string.Join("\n", lines.Skip(ti + 1)) });
            }
            return cues;
        }

        private static string Norm(string t)
        {
            t = t.Replace(',', '.');
            if (t.Count(c => c == ':') == 1) t = "00:" + t;
            return t;
        }

        public static void Write(List<Cue> cues, string path, string id)
        {
            var sb = new StringBuilder();
            if (id == "vtt") sb.Append("WEBVTT\n\n");
            for (int i = 0; i < cues.Count; i++)
            {
                if (id == "srt") sb.Append(i + 1).Append('\n');
                string s = cues[i].Start, e = cues[i].End;
                if (id == "srt") { s = s.Replace('.', ','); e = e.Replace('.', ','); }
                sb.Append(s).Append(" --> ").Append(e).Append('\n').Append(cues[i].Text).Append("\n\n");
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
