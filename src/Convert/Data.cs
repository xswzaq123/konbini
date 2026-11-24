using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace Konbini.Conversion
{
    // Tabular data: first row is the header.
    public static class Tables
    {
        public static List<List<string>> Read(string path, string id, out string note)
        {
            note = "";
            switch (id)
            {
                case "csv": return ParseDelimited(DocReaders.ReadText(path), DetectDelimiter(DocReaders.ReadText(path)));
                case "tsv": return ParseDelimited(DocReaders.ReadText(path), '\t');
                case "xlsx": return ReadXlsx(path, out note);
                case "ods": return ReadOds(path, out note);
                case "json": return FromJson(Json.Parse(DocReaders.ReadText(path)));
                case "xml": return FromXml(XDocument.Load(path));
                default: throw new NotSupportedException("Can't read ." + id + " as a table.");
            }
        }

        public static void Write(List<List<string>> rows, string path, string id)
        {
            var utf8 = new UTF8Encoding(false);
            switch (id)
            {
                case "csv": File.WriteAllText(path, ToDelimited(rows, ','), new UTF8Encoding(true)); break; // BOM so Excel reads UTF-8
                case "tsv": File.WriteAllText(path, ToDelimited(rows, '\t'), utf8); break;
                case "xlsx": WriteXlsx(rows, path); break;
                case "json": File.WriteAllText(path, Json.Pretty(ToRecords(rows)), utf8); break;
                case "xml": ToXml(rows).Save(path); break;
                case "html": { var d = new Doc(); d.Table(rows); File.WriteAllText(path, DocWriters.ToHtml(d), utf8); break; }
                case "md": { var d = new Doc(); d.Table(rows); File.WriteAllText(path, DocWriters.ToMarkdown(d), utf8); break; }
                case "pdf": { var d = new Doc(); d.Table(rows); DocWriters.ToPdf(d, path); break; }
                default: throw new NotSupportedException("Can't write tables as ." + id);
            }
        }

        private static char DetectDelimiter(string text)
        {
            string first = text.Split('\n')[0];
            var cands = new[] { ',', ';', '\t', '|' };
            return cands.OrderByDescending(c => first.Count(ch => ch == c)).First();
        }

        public static List<List<string>> ParseDelimited(string text, char sep)
        {
            var rows = new List<List<string>>();
            var row = new List<string>(); var cell = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = false; }
                    else cell.Append(c);
                }
                else if (c == '"' && cell.Length == 0) quoted = true;
                else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString()); cell.Clear();
                    if (!(row.Count == 1 && row[0].Length == 0)) rows.Add(row);
                    row = new List<string>();
                }
                else if (c != '﻿') cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
            return rows;
        }

        public static string ToDelimited(List<List<string>> rows, char sep)
        {
            var sb = new StringBuilder();
            foreach (var r in rows)
            {
                sb.Append(string.Join(sep.ToString(), r.Select(c =>
                {
                    c = c ?? "";
                    if (sep == '\t') return c.Replace("\t", " ").Replace("\r", "").Replace("\n", " ");
                    return c.IndexOfAny(new[] { sep, '"', '\n', '\r' }) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c;
                })));
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        // ----- JSON / XML <-> table -----

        public static List<Dictionary<string, object>> ToRecords(List<List<string>> rows)
        {
            var list = new List<Dictionary<string, object>>();
            if (rows.Count == 0) return list;
            var header = UniqueHeader(rows[0]);
            foreach (var r in rows.Skip(1))
            {
                var d = new Dictionary<string, object>();
                for (int i = 0; i < header.Count; i++) d[header[i]] = Typed(i < r.Count ? r[i] : "");
                list.Add(d);
            }
            return list;
        }

        private static List<string> UniqueHeader(List<string> h)
        {
            var seen = new HashSet<string>(); var outp = new List<string>();
            for (int i = 0; i < h.Count; i++)
            {
                string k = string.IsNullOrWhiteSpace(h[i]) ? "column" + (i + 1) : h[i].Trim(), u = k; int n = 2;
                while (!seen.Add(u)) u = k + "_" + n++;
                outp.Add(u);
            }
            return outp;
        }

        private static object Typed(string s)
        {
            double d;
            if (Regex.IsMatch(s, @"^-?(0|[1-9]\d*)(\.\d+)?([eE][-+]?\d+)?$") && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            {
                long l; if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
                return d;
            }
            if (s == "true") return true;
            if (s == "false") return false;
            return s;
        }

        public static List<List<string>> FromJson(object root)
        {
            var list = root as ArrayList;
            if (list == null)
            {
                // { "items": [...] } — use the first array property.
                var dict = root as Dictionary<string, object>;
                if (dict != null) list = dict.Values.OfType<ArrayList>().FirstOrDefault();
                if (list == null && dict != null) list = new ArrayList { dict };
            }
            if (list == null) throw new InvalidDataException("JSON must contain a list of records to become a table.");
            var rows = new List<Dictionary<string, string>>();
            var columns = new List<string>(); var colSet = new HashSet<string>();
            foreach (var item in list)
            {
                var flat = new Dictionary<string, string>();
                var arr = item as ArrayList;
                if (arr != null) { for (int i = 0; i < arr.Count; i++) flat["column" + (i + 1)] = Json.Scalar(arr[i]); }
                else Flatten(item, "", flat);
                foreach (var k in flat.Keys) if (colSet.Add(k)) columns.Add(k);
                rows.Add(flat);
            }
            var table = new List<List<string>> { columns };
            foreach (var r in rows) table.Add(columns.Select(c => { string v; return r.TryGetValue(c, out v) ? v : ""; }).ToList());
            return table;
        }

        private static void Flatten(object o, string prefix, Dictionary<string, string> into)
        {
            var d = o as Dictionary<string, object>;
            if (d != null) { foreach (var kv in d) Flatten(kv.Value, prefix.Length == 0 ? kv.Key : prefix + "." + kv.Key, into); return; }
            var a = o as ArrayList;
            if (a != null && a.Cast<object>().All(x => !(x is Dictionary<string, object>) && !(x is ArrayList)))
            { into[prefix.Length == 0 ? "value" : prefix] = string.Join("; ", a.Cast<object>().Select(Json.Scalar)); return; }
            if (a != null) { for (int i = 0; i < a.Count; i++) Flatten(a[i], prefix + "[" + i + "]", into); return; }
            into[prefix.Length == 0 ? "value" : prefix] = Json.Scalar(o);
        }

        public static List<List<string>> FromXml(XDocument x)
        {
            // Records = the most common repeated child element under the root.
            var root = x.Root;
            var group = root.Descendants().Where(e => e.HasElements || e.HasAttributes).GroupBy(e => e.Name)
                .Where(g => g.Count() > 1).OrderByDescending(g => g.Count()).FirstOrDefault();
            var records = group != null ? group.ToList() : root.Elements().ToList();
            var json = records.Select(r => (object)Json.FromXElement(r)).ToList();
            var arr = new ArrayList(); foreach (var j in json) arr.Add(j is Dictionary<string, object> ? j : new Dictionary<string, object> { { "value", j } });
            return FromJson(arr);
        }

        public static XDocument ToXml(List<List<string>> rows)
        {
            var root = new XElement("rows");
            if (rows.Count > 0)
            {
                var header = UniqueHeader(rows[0]).Select(Json.XmlName).ToList();
                foreach (var r in rows.Skip(1))
                    root.Add(new XElement("row", header.Select((h, i) => new XElement(h, i < r.Count ? r[i] : ""))));
            }
            return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        }

        // ----- Excel (.xlsx) -----

        private static XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        private static List<List<string>> ReadXlsx(string path, out string note)
        {
            note = "";
            using (var z = ZipFile.OpenRead(path))
            {
                var shared = new List<string>();
                var ss = z.GetEntry("xl/sharedStrings.xml");
                if (ss != null) using (var s = ss.Open())
                    shared = XDocument.Load(s).Root.Elements(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList();

                // First sheet in workbook order.
                XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
                XDocument wb; using (var s = z.GetEntry("xl/workbook.xml").Open()) wb = XDocument.Load(s);
                var sheets = wb.Descendants(S + "sheet").ToList();
                if (sheets.Count > 1) note = " (first of " + sheets.Count + " sheets)";
                string sheetPath = "xl/worksheets/sheet1.xml";
                var relsEntry = z.GetEntry("xl/_rels/workbook.xml.rels");
                if (relsEntry != null && sheets.Count > 0)
                {
                    XDocument rels; using (var s = relsEntry.Open()) rels = XDocument.Load(s);
                    string rid = (string)sheets[0].Attribute(r + "id");
                    var rel = rels.Root.Elements().FirstOrDefault(e => (string)e.Attribute("Id") == rid);
                    if (rel != null)
                    {
                        string target = ((string)rel.Attribute("Target")).TrimStart('/');
                        sheetPath = target.StartsWith("xl/") ? target : "xl/" + target;
                    }
                }
                XDocument sheet; using (var s = z.GetEntry(sheetPath).Open()) sheet = XDocument.Load(s);
                var rows = new List<List<string>>();
                foreach (var row in sheet.Descendants(S + "row"))
                {
                    var cells = new List<string>();
                    foreach (var c in row.Elements(S + "c"))
                    {
                        int col = ColIndex((string)c.Attribute("r"));
                        if (col < 0) col = cells.Count;
                        while (cells.Count < col) cells.Add("");
                        string t = (string)c.Attribute("t"), v = (string)c.Element(S + "v");
                        string val;
                        if (t == "s" && v != null) val = shared[int.Parse(v)];
                        else if (t == "inlineStr") val = string.Concat(c.Descendants(S + "t").Select(x => x.Value));
                        else if (t == "b") val = v == "1" ? "TRUE" : "FALSE";
                        else val = v ?? "";
                        cells.Add(val);
                    }
                    int rn; int.TryParse((string)row.Attribute("r"), out rn);
                    while (rn > 0 && rows.Count < rn - 1) rows.Add(new List<string>());
                    rows.Add(cells);
                }
                return rows;
            }
        }

        private static int ColIndex(string cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return -1;
            int n = 0;
            foreach (char ch in cellRef) { if (!char.IsLetter(ch)) break; n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1); }
            return n - 1;
        }

        private static string ColName(int i)
        {
            string s = ""; i++;
            while (i > 0) { int m = (i - 1) % 26; s = (char)('A' + m) + s; i = (i - 1) / 26; }
            return s;
        }

        private static void WriteXlsx(List<List<string>> rows, string path)
        {
            var sd = new StringBuilder();
            for (int r = 0; r < rows.Count; r++)
            {
                sd.Append("<row r=\"").Append(r + 1).Append("\">");
                for (int c = 0; c < rows[r].Count; c++)
                {
                    string v = rows[r][c] ?? "", refc = ColName(c) + (r + 1);
                    double d;
                    bool num = r > 0 && Regex.IsMatch(v, @"^-?\d+(\.\d+)?$") && v.Length < 16 && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                    if (num) sd.Append("<c r=\"").Append(refc).Append("\"><v>").Append(v).Append("</v></c>");
                    else sd.Append("<c r=\"").Append(refc).Append("\" t=\"inlineStr\"").Append(r == 0 ? " s=\"1\"" : "").Append("><is><t xml:space=\"preserve\">")
                           .Append(System.Security.SecurityElement.Escape(Regex.Replace(v, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", ""))).Append("</t></is></c>");
                }
                sd.Append("</row>");
            }
            const string X = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
            OpenXml.WritePackage(path, new Dictionary<string, string>
            {
                { "[Content_Types].xml", X + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                    "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>" },
                { "_rels/.rels", X + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>" },
                { "xl/workbook.xml", X + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>" },
                { "xl/_rels/workbook.xml.rels", X + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                    "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>" },
                { "xl/styles.xml", X + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                    "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border/></borders>" +
                    "<cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"2\"><xf/><xf fontId=\"1\" applyFont=\"1\"/></cellXfs></styleSheet>" },
                { "xl/worksheets/sheet1.xml", X + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><sheetData>" + sd + "</sheetData></worksheet>" },
            });
        }

        private static List<List<string>> ReadOds(string path, out string note)
        {
            note = "";
            XNamespace table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
            XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
            using (var z = ZipFile.OpenRead(path))
            {
                XDocument x; using (var s = z.GetEntry("content.xml").Open()) x = XDocument.Load(s);
                var tables = x.Descendants(table + "table").ToList();
                if (tables.Count > 1) note = " (first of " + tables.Count + " sheets)";
                var rows = new List<List<string>>();
                foreach (var row in tables[0].Descendants(table + "table-row"))
                {
                    int rrep = Math.Min(1000, (int?)row.Attribute(table + "number-rows-repeated") ?? 1);
                    var cells = new List<string>();
                    foreach (var c in row.Elements().Where(e => e.Name == table + "table-cell" || e.Name == table + "covered-table-cell"))
                    {
                        int crep = Math.Min(256, (int?)c.Attribute(table + "number-columns-repeated") ?? 1);
                        string v = string.Join("\n", c.Elements(text + "p").Select(p => p.Value));
                        for (int i = 0; i < crep; i++) cells.Add(v);
                    }
                    while (cells.Count > 0 && cells[cells.Count - 1].Length == 0) cells.RemoveAt(cells.Count - 1);
                    for (int i = 0; i < rrep; i++) rows.Add(new List<string>(cells));
                }
                while (rows.Count > 0 && rows[rows.Count - 1].Count == 0) rows.RemoveAt(rows.Count - 1);
                return rows;
            }
        }
    }

    // JSON helpers on the built-in serializer, plus JSON <-> XML / YAML tree conversion.
    public static class Json
    {
        private static JavaScriptSerializer Ser() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 }; }

        // The built-in serializer returns arrays as object[]; normalise to ArrayList so the rest of the code sees one list type.
        public static object Parse(string text) { return Normalize(Ser().DeserializeObject(text)); }

        private static object Normalize(object o)
        {
            var arr = o as object[];
            if (arr != null) { var l = new ArrayList(arr.Length); foreach (var x in arr) l.Add(Normalize(x)); return l; }
            var d = o as Dictionary<string, object>;
            if (d != null) foreach (var k in d.Keys.ToList()) d[k] = Normalize(d[k]);
            return o;
        }

        public static string Scalar(object o)
        {
            if (o == null) return "";
            if (o is bool) return (bool)o ? "true" : "false";
            if (o is decimal) return ((decimal)o).ToString(CultureInfo.InvariantCulture);
            if (o is double) return ((double)o).ToString("R", CultureInfo.InvariantCulture);
            if (o is Dictionary<string, object> || o is ArrayList || o is object[]) return Ser().Serialize(o);
            return Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        public static string Pretty(object o)
        {
            var sb = new StringBuilder(); Write(sb, o, 0); return sb.ToString();
        }

        private static void Write(StringBuilder sb, object o, int ind)
        {
            string pad = new string(' ', ind * 2), pad2 = new string(' ', ind * 2 + 2);
            var d = o as IDictionary;
            if (d != null)
            {
                if (d.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n"); int i = 0;
                foreach (DictionaryEntry kv in d)
                {
                    sb.Append(pad2).Append(Ser().Serialize(kv.Key.ToString())).Append(": ");
                    Write(sb, kv.Value, ind + 1);
                    sb.Append(++i < d.Count ? ",\n" : "\n");
                }
                sb.Append(pad).Append('}'); return;
            }
            var l = o as IEnumerable;
            if (l != null && !(o is string))
            {
                var items = l.Cast<object>().ToList();
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < items.Count; i++) { sb.Append(pad2); Write(sb, items[i], ind + 1); sb.Append(i < items.Count - 1 ? ",\n" : "\n"); }
                sb.Append(pad).Append(']'); return;
            }
            sb.Append(Ser().Serialize(o));
        }

        public static string XmlName(string s)
        {
            string n = Regex.Replace(s ?? "", @"[^\w.-]", "_");
            if (n.Length == 0 || !(char.IsLetter(n[0]) || n[0] == '_')) n = "_" + n;
            return n;
        }

        public static XDocument ToXml(object root)
        {
            var el = new XElement("root");
            Fill(el, root);
            return new XDocument(new XDeclaration("1.0", "utf-8", null), el);
        }

        private static void Fill(XElement el, object o)
        {
            var d = o as Dictionary<string, object>;
            if (d != null)
            {
                foreach (var kv in d)
                {
                    var arr = kv.Value as ArrayList;
                    if (arr != null) foreach (var item in arr) { var c = new XElement(XmlName(kv.Key)); Fill(c, item); el.Add(c); }
                    else { var c = new XElement(XmlName(kv.Key)); Fill(c, kv.Value); el.Add(c); }
                }
                return;
            }
            var a = o as ArrayList;
            if (a != null) { foreach (var item in a) { var c = new XElement("item"); Fill(c, item); el.Add(c); } return; }
            el.Value = Scalar(o);
        }

        public static object FromXElement(XElement e)
        {
            if (!e.HasElements && !e.HasAttributes) return e.Value;
            var d = new Dictionary<string, object>();
            foreach (var a in e.Attributes()) if (!a.IsNamespaceDeclaration) d["@" + a.Name.LocalName] = a.Value;
            foreach (var g in e.Elements().GroupBy(c => c.Name.LocalName))
            {
                var items = g.Select(FromXElement).ToList();
                if (items.Count == 1) d[g.Key] = items[0];
                else { var arr = new ArrayList(); foreach (var i in items) arr.Add(i); d[g.Key] = arr; }
            }
            string text = string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
            if (text.Length > 0) d["#text"] = text;
            return d;
        }

        public static string ToYaml(object o)
        {
            var sb = new StringBuilder(); Yaml(sb, o, 0, false); return sb.ToString();
        }

        private static void Yaml(StringBuilder sb, object o, int ind, bool inline)
        {
            string pad = new string(' ', ind * 2);
            var d = o as Dictionary<string, object>;
            var a = o as ArrayList;
            if (d != null && d.Count > 0)
            {
                bool first = true;
                foreach (var kv in d)
                {
                    if (!(inline && first)) sb.Append(pad);
                    first = false;
                    sb.Append(YamlKey(kv.Key)).Append(':');
                    if ((kv.Value is Dictionary<string, object> && ((Dictionary<string, object>)kv.Value).Count > 0) ||
                        (kv.Value is ArrayList && ((ArrayList)kv.Value).Count > 0)) { sb.Append('\n'); Yaml(sb, kv.Value, ind + 1, false); }
                    else { sb.Append(' '); Yaml(sb, kv.Value, ind + 1, true); }
                }
                return;
            }
            if (a != null && a.Count > 0)
            {
                foreach (var item in a)
                {
                    sb.Append(pad).Append("- ");
                    if (item is Dictionary<string, object> && ((Dictionary<string, object>)item).Count > 0) Yaml(sb, item, ind + 1, true);
                    else if (item is ArrayList && ((ArrayList)item).Count > 0) { sb.Append('\n'); Yaml(sb, item, ind + 1, false); }
                    else Yaml(sb, item, ind + 1, true);
                }
                return;
            }
            if (d != null) { sb.Append("{}\n"); return; }
            if (a != null) { sb.Append("[]\n"); return; }
            if (o == null) { sb.Append("null\n"); return; }
            if (o is bool || o is int || o is long || o is decimal || o is double) { sb.Append(Scalar(o)).Append('\n'); return; }
            string s = Scalar(o);
            bool quote = s.Length == 0 || Regex.IsMatch(s, @"^[\s\-?:,\[\]{}#&*!|>'""%@`]|[:#]\s|\s$|^(true|false|null|yes|no|~)$|^-?\d", RegexOptions.IgnoreCase) || s.Contains("\n");
            sb.Append(quote ? Ser().Serialize(s) : s).Append('\n');
        }

        private static string YamlKey(string k)
        {
            return Regex.IsMatch(k, @"^[A-Za-z_][\w.-]*$") ? k : Ser().Serialize(k);
        }
    }
}
