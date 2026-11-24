using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Konbini.Conversion
{
    // Tiny PDF 1.4 writer: text pages (Helvetica, WinAnsi) and full-page JPEG images. No external libraries.
    public class PdfWriter
    {
        public const float PageW = 595.28f, PageH = 841.89f, Margin = 56f; // A4

        private readonly List<byte[]> objects = new List<byte[]>();   // object bodies, index+1 = object number
        private readonly List<int> pageIds = new List<int>();
        private readonly int fontRegular, fontBold, fontMono;

        // Current text page state
        private StringBuilder content;
        private float y;

        public PdfWriter()
        {
            fontRegular = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            fontBold = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
            fontMono = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>");
        }

        private int Add(string body) { return Add(Encoding.ASCII.GetBytes(body)); }
        private int Add(byte[] body) { objects.Add(body); return objects.Count; }
        private int Reserve() { objects.Add(null); return objects.Count; }

        private static string F(float v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // ---------- images ----------

        public void AddJpegPage(byte[] jpeg, int pxW, int pxH)
        {
            FlushTextPage();
            // 96 dpi → points, shrunk to fit A4 if larger.
            float w = pxW * 0.75f, h = pxH * 0.75f;
            float s = Math.Min(1f, Math.Min(PageW / w, PageH / h));
            if (w < 200 && h < 200) s = Math.Min(PageW / w, PageH / h) * 0.5f; // tiny images: scale up a bit
            w *= s; h *= s;
            var head = Encoding.ASCII.GetBytes("<< /Type /XObject /Subtype /Image /Width " + pxW + " /Height " + pxH +
                " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpeg.Length + " >>\nstream\n");
            var tail = Encoding.ASCII.GetBytes("\nendstream");
            var img = new byte[head.Length + jpeg.Length + tail.Length];
            Buffer.BlockCopy(head, 0, img, 0, head.Length);
            Buffer.BlockCopy(jpeg, 0, img, head.Length, jpeg.Length);
            Buffer.BlockCopy(tail, 0, img, head.Length + jpeg.Length, tail.Length);
            int imgId = Add(img);
            string draw = "q " + F(w) + " 0 0 " + F(h) + " 0 0 cm /Im0 Do Q";
            int contentId = AddStream(draw);
            AddPage(w, h, "/XObject << /Im0 " + imgId + " 0 R >>", contentId);
        }

        // ---------- text ----------

        public enum Style { Body, Bold, H1, H2, H3, Mono, Small }

        private static void FontFor(Style s, out string font, out float size)
        {
            switch (s)
            {
                case Style.H1: font = "/F2"; size = 20; break;
                case Style.H2: font = "/F2"; size = 15; break;
                case Style.H3: font = "/F2"; size = 12.5f; break;
                case Style.Bold: font = "/F2"; size = 10.5f; break;
                case Style.Mono: font = "/F3"; size = 9; break;
                case Style.Small: font = "/F1"; size = 8.5f; break;
                default: font = "/F1"; size = 10.5f; break;
            }
        }

        // Writes a wrapped paragraph; indent in points. spaceAfter adds a gap below.
        public void Paragraph(string text, Style style = Style.Body, float indent = 0, float spaceAfter = 6)
        {
            string font; float size;
            FontFor(style, out font, out size);
            float lineH = size * 1.35f;
            float maxW = PageW - 2 * Margin - indent;
            if (style == Style.H1 || style == Style.H2) Space(size * 0.4f);
            foreach (var rawLine in (text ?? "").Replace("\r", "").Split('\n'))
            {
                foreach (var line in Wrap(rawLine.Replace("\t", "    "), font, size, maxW))
                {
                    EnsureRoom(lineH);
                    y -= lineH;
                    content.Append("BT ").Append(font).Append(' ').Append(F(size)).Append(" Tf ")
                           .Append(F(Margin + indent)).Append(' ').Append(F(y + lineH * 0.25f)).Append(" Td (")
                           .Append(Escape(line)).Append(") Tj ET\n");
                }
            }
            Space(spaceAfter);
        }

        // Simple table: columns share the width equally, cells wrap, header row bold with a rule beneath.
        public void Table(List<List<string>> rows)
        {
            if (rows.Count == 0) return;
            int cols = 0; foreach (var r in rows) cols = Math.Max(cols, r.Count);
            if (cols == 0) return;
            float size = cols > 6 ? 7.5f : 9f, lineH = size * 1.3f, pad = 3;
            float colW = (PageW - 2 * Margin) / cols;
            for (int ri = 0; ri < rows.Count; ri++)
            {
                string font = ri == 0 ? "/F2" : "/F1";
                var wrapped = new List<List<string>>();
                int lines = 1;
                for (int c = 0; c < cols; c++)
                {
                    string cell = c < rows[ri].Count ? rows[ri][c] ?? "" : "";
                    var w = Wrap(cell.Replace("\n", " "), font, size, colW - 2 * pad);
                    wrapped.Add(w); lines = Math.Max(lines, w.Count);
                }
                float rowH = lines * lineH + pad;
                EnsureRoom(rowH);
                for (int c = 0; c < cols; c++)
                    for (int l = 0; l < wrapped[c].Count; l++)
                        content.Append("BT ").Append(font).Append(' ').Append(F(size)).Append(" Tf ")
                               .Append(F(Margin + c * colW + pad)).Append(' ').Append(F(y - (l + 1) * lineH + lineH * 0.25f))
                               .Append(" Td (").Append(Escape(wrapped[c][l])).Append(") Tj ET\n");
                y -= rowH;
                float gray = ri == 0 ? 0.35f : 0.85f;
                content.Append(F(gray)).Append(" G 0.5 w ").Append(F(Margin)).Append(' ').Append(F(y)).Append(" m ")
                       .Append(F(PageW - Margin)).Append(' ').Append(F(y)).Append(" l S\n");
            }
            Space(8);
        }

        public void Space(float pts) { if (content != null) y -= pts; }

        public void PageBreak() { FlushTextPage(); }

        private void EnsureRoom(float h)
        {
            if (content == null) { content = new StringBuilder(); y = PageH - Margin; }
            if (y - h < Margin) { FlushTextPage(); content = new StringBuilder(); y = PageH - Margin; }
        }

        private void FlushTextPage()
        {
            if (content == null) return;
            int contentId = AddStream(content.ToString());
            AddPage(PageW, PageH, "/Font << /F1 " + fontRegular + " 0 R /F2 " + fontBold + " 0 R /F3 " + fontMono + " 0 R >>", contentId);
            content = null;
        }

        private int AddStream(string s)
        {
            var data = Encoding.GetEncoding(1252).GetBytes(s);
            var head = Encoding.ASCII.GetBytes("<< /Length " + data.Length + " >>\nstream\n");
            var tail = Encoding.ASCII.GetBytes("\nendstream");
            var all = new byte[head.Length + data.Length + tail.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(data, 0, all, head.Length, data.Length);
            Buffer.BlockCopy(tail, 0, all, head.Length + data.Length, tail.Length);
            return Add(all);
        }

        private void AddPage(float w, float h, string resources, int contentId)
        {
            int id = Add("<< /Type /Page /Parent PAGES_REF /MediaBox [0 0 " + F(w) + " " + F(h) + "] /Resources << " + resources +
                         " >> /Contents " + contentId + " 0 R >>");
            pageIds.Add(id);
        }

        public void Save(string path)
        {
            FlushTextPage();
            if (pageIds.Count == 0) { Paragraph(" "); FlushTextPage(); }
            int pagesId = Reserve();
            var kids = new StringBuilder();
            foreach (var p in pageIds) kids.Append(p).Append(" 0 R ");
            objects[pagesId - 1] = Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [" + kids + "] /Count " + pageIds.Count + " >>");
            string pagesRef = pagesId + " 0 R";
            foreach (var p in pageIds)
                objects[p - 1] = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(objects[p - 1]).Replace("PAGES_REF", pagesRef));
            int catalog = Add("<< /Type /Catalog /Pages " + pagesRef + " >>");
            int info = Add("<< /Producer (Konbini) >>");

            using (var fs = File.Create(path))
            {
                var offsets = new List<long>();
                WriteAscii(fs, "%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
                for (int i = 0; i < objects.Count; i++)
                {
                    offsets.Add(fs.Position);
                    WriteAscii(fs, (i + 1) + " 0 obj\n");
                    fs.Write(objects[i], 0, objects[i].Length);
                    WriteAscii(fs, "\nendobj\n");
                }
                long xref = fs.Position;
                var sb = new StringBuilder();
                sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
                foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
                sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root ").Append(catalog).Append(" 0 R /Info ")
                  .Append(info).Append(" 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
                WriteAscii(fs, sb.ToString());
            }
        }

        private static void WriteAscii(Stream s, string t) { var b = Encoding.GetEncoding(1252).GetBytes(t); s.Write(b, 0, b.Length); }

        // ---------- text helpers ----------

        private static string Escape(string s)
        {
            var enc = Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
            string safe = enc.GetString(enc.GetBytes(s));
            var sb = new StringBuilder(safe.Length);
            foreach (char c in safe)
            {
                if (c == '\\' || c == '(' || c == ')') sb.Append('\\').Append(c);
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static readonly short[] Helv = {
            278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556,
            1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556,
            333,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,556,556,333,500,278,556,500,722,500,500,500,334,260,334,584 };

        public static float TextWidth(string s, string font, float size)
        {
            if (font == "/F3") return s.Length * 600 * size / 1000f;
            float w = 0;
            foreach (char c in s) w += (c >= 32 && c <= 126) ? Helv[c - 32] : 556;
            if (font == "/F2") w *= 1.06f;
            return w * size / 1000f;
        }

        private static List<string> Wrap(string text, string font, float size, float maxW)
        {
            var lines = new List<string>();
            if (text.Length == 0) { lines.Add(""); return lines; }
            var line = new StringBuilder();
            foreach (var word in text.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (TextWidth(candidate, font, size) <= maxW) { line.Clear(); line.Append(candidate); continue; }
                if (line.Length > 0) { lines.Add(line.ToString()); line.Clear(); }
                // Break words longer than a full line.
                string rest = word;
                while (TextWidth(rest, font, size) > maxW && rest.Length > 1)
                {
                    int n = rest.Length;
                    while (n > 1 && TextWidth(rest.Substring(0, n), font, size) > maxW) n--;
                    lines.Add(rest.Substring(0, n)); rest = rest.Substring(n);
                }
                line.Append(rest);
            }
            lines.Add(line.ToString());
            return lines;
        }
    }
}
