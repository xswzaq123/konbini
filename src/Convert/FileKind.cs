using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Konbini.Conversion
{
    public enum Category { Image, Audio, Video, Pdf, Document, Data, Archive, Other }

    public class FileKind
    {
        public readonly string Id;        // canonical lower-case extension, e.g. "jpg"
        public readonly string Name;      // human label, e.g. "JPEG image"
        public readonly Category Category;
        public FileKind(string id, string name, Category c) { Id = id; Name = name; Category = c; }
        public override string ToString() { return Name + " (." + Id + ")"; }
    }

    // Identifies files by their content (magic bytes), falling back to the extension for plain-text formats.
    public static class FileDetector
    {
        public static FileKind Detect(string path)
        {
            byte[] h = new byte[64];
            int n;
            try { using (var fs = File.OpenRead(path)) n = fs.Read(h, 0, h.Length); }
            catch { return Unknown(path); }
            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            if (n >= 8 && h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G') return K("png");
            if (n >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return K("jpg");
            if (n >= 6 && Ascii(h, 0, 4) == "GIF8") return K("gif");
            if (n >= 2 && h[0] == 'B' && h[1] == 'M') return K("bmp");
            if (n >= 4 && ((h[0] == 'I' && h[1] == 'I' && h[2] == 42 && h[3] == 0) || (h[0] == 'M' && h[1] == 'M' && h[2] == 0 && h[3] == 42))) return K("tif");
            if (n >= 4 && h[0] == 'I' && h[1] == 'I' && h[2] == 0xBC) return K("jxr");
            if (n >= 4 && h[0] == 0 && h[1] == 0 && h[2] == 1 && h[3] == 0) return K("ico");
            if (n >= 12 && Ascii(h, 0, 4) == "RIFF")
            {
                string t = Ascii(h, 8, 4);
                if (t == "WEBP") return K("webp");
                if (t == "WAVE") return K("wav");
                if (t == "AVI ") return K("avi");
            }
            if (n >= 5 && Ascii(h, 0, 5) == "%PDF-") return K("pdf");
            if (n >= 4 && Ascii(h, 0, 4) == "fLaC") return K("flac");
            if (n >= 4 && Ascii(h, 0, 4) == "OggS") return K("ogg");
            if (n >= 3 && Ascii(h, 0, 3) == "ID3") return K("mp3");
            if (n >= 2 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0 && (h[1] & 0x06) != 0) return ext == "aac" ? K("aac") : K("mp3");
            if (n >= 2 && h[0] == 0xFF && (h[1] & 0xF6) == 0xF0) return K("aac"); // ADTS
            if (n >= 12 && Ascii(h, 4, 4) == "ftyp")
            {
                string brand = Ascii(h, 8, 4);
                if (brand == "heic" || brand == "heix" || brand == "mif1" || brand == "msf1" || brand == "hevc") return K("heic");
                if (brand == "avif" || brand == "avis") return K("avif");
                if (brand == "M4A " || brand == "M4B ") return K("m4a");
                if (brand == "qt  ") return K("mov");
                if (brand.StartsWith("3g")) return K("3gp");
                return ext == "m4a" ? K("m4a") : ext == "m4v" ? K("m4v") : ext == "mov" ? K("mov") : K("mp4");
            }
            if (n >= 4 && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3)
                return Ascii(h, 0, n).Contains("webm") ? K("webm") : K("mkv");
            if (n >= 16 && h[0] == 0x30 && h[1] == 0x26 && h[2] == 0xB2 && h[3] == 0x75) return ext == "wma" ? K("wma") : K("wmv");
            if (n >= 2 && h[0] == 0x1F && h[1] == 0x8B) return ext == "tgz" || path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? K("tgz") : K("gz");
            if (n >= 6 && h[0] == '7' && h[1] == 'z' && h[2] == 0xBC && h[3] == 0xAF) return K("7z");
            if (n >= 4 && Ascii(h, 0, 4) == "Rar!") return K("rar");
            if (n >= 5 && Ascii(h, 0, 5) == "{\\rtf") return K("rtf");
            if (n >= 4 && h[0] == 'P' && h[1] == 'K' && h[2] == 3 && h[3] == 4) return DetectZip(path);
            if (IsTar(path)) return K("tar");
            if (LooksLikeText(path)) return DetectText(path, ext);
            return Unknown(path);
        }

        private static FileKind DetectZip(string path)
        {
            try
            {
                using (var z = ZipFile.OpenRead(path))
                {
                    var names = z.Entries.Select(e => e.FullName).ToList();
                    var mime = z.GetEntry("mimetype");
                    if (mime != null)
                    {
                        string m; using (var r = new StreamReader(mime.Open())) m = r.ReadToEnd().Trim();
                        if (m == "application/epub+zip") return K("epub");
                        if (m.EndsWith("opendocument.text")) return K("odt");
                        if (m.EndsWith("opendocument.spreadsheet")) return K("ods");
                    }
                    if (names.Contains("word/document.xml")) return K("docx");
                    if (names.Contains("xl/workbook.xml")) return K("xlsx");
                    if (names.Contains("ppt/presentation.xml")) return K("pptx");
                }
            }
            catch { }
            return K("zip");
        }

        private static bool IsTar(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    if (fs.Length < 512) return false;
                    var b = new byte[512]; fs.Read(b, 0, 512);
                    return Ascii(b, 257, 5) == "ustar";
                }
            }
            catch { return false; }
        }

        private static bool LooksLikeText(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var b = new byte[Math.Min(8192, (int)fs.Length)];
                    int n = fs.Read(b, 0, b.Length);
                    if (n == 0) return true;
                    if (n >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF))) return true; // UTF-16 BOM
                    int bad = 0;
                    for (int i = 0; i < n; i++) { byte c = b[i]; if (c == 0) return false; if (c < 9 || (c > 13 && c < 32)) bad++; }
                    return bad * 100 < n;
                }
            }
            catch { return false; }
        }

        private static FileKind DetectText(string path, string ext)
        {
            string head;
            using (var r = new StreamReader(path, Encoding.UTF8, true))
            {
                var buf = new char[4096]; int n = r.Read(buf, 0, buf.Length); head = new string(buf, 0, n);
            }
            string t = head.TrimStart('﻿', ' ', '\t', '\r', '\n');
            string lower = t.ToLowerInvariant();
            if (lower.StartsWith("<!doctype html") || lower.StartsWith("<html") || ext == "html" || ext == "htm") return K("html");
            if (lower.StartsWith("<svg") || (lower.StartsWith("<?xml") && lower.Contains("<svg"))) return K("svg");
            if (lower.StartsWith("<?xml") || (t.StartsWith("<") && ext == "xml")) return K("xml");
            if ((t.StartsWith("{") || t.StartsWith("[")) && (ext == "json" || ext == "" || ext == "txt")) return K("json");
            switch (ext)
            {
                case "csv": return K("csv");
                case "tsv": case "tab": return K("tsv");
                case "md": case "markdown": return K("md");
                case "json": return K("json");
                case "xml": return K("xml");
                case "yaml": case "yml": return K("yaml");
                case "srt": return K("srt");
                case "vtt": return K("vtt");
            }
            // Comma-separated content with consistent column counts → CSV.
            var lines = t.Split('\n').Take(6).Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count >= 2 && lines.All(l => l.Contains(",")) && lines.Select(l => l.Count(ch => ch == ',')).Distinct().Count() == 1) return K("csv");
            if (lines.Count >= 2 && lines.All(l => l.Contains("\t")) && lines.Select(l => l.Count(ch => ch == '\t')).Distinct().Count() == 1) return K("tsv");
            return K("txt");
        }

        private static FileKind Unknown(string path)
        {
            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return new FileKind(ext == "" ? "bin" : ext, (ext == "" ? "Unknown" : ext.ToUpperInvariant()) + " file", Category.Other);
        }

        private static string Ascii(byte[] b, int off, int len)
        {
            if (off + len > b.Length) return "";
            return Encoding.ASCII.GetString(b, off, len);
        }

        public static FileKind K(string id)
        {
            switch (id)
            {
                case "png": return new FileKind(id, "PNG image", Category.Image);
                case "jpg": return new FileKind(id, "JPEG image", Category.Image);
                case "gif": return new FileKind(id, "GIF image", Category.Image);
                case "bmp": return new FileKind(id, "Bitmap image", Category.Image);
                case "tif": return new FileKind(id, "TIFF image", Category.Image);
                case "ico": return new FileKind(id, "Icon", Category.Image);
                case "webp": return new FileKind(id, "WebP image", Category.Image);
                case "heic": return new FileKind(id, "HEIC/HEIF image", Category.Image);
                case "avif": return new FileKind(id, "AVIF image", Category.Image);
                case "jxr": return new FileKind(id, "JPEG XR image", Category.Image);
                case "svg": return new FileKind(id, "SVG vector image", Category.Document);
                case "mp3": return new FileKind(id, "MP3 audio", Category.Audio);
                case "wav": return new FileKind(id, "WAV audio", Category.Audio);
                case "flac": return new FileKind(id, "FLAC audio", Category.Audio);
                case "m4a": return new FileKind(id, "M4A / AAC audio", Category.Audio);
                case "aac": return new FileKind(id, "AAC audio", Category.Audio);
                case "wma": return new FileKind(id, "WMA audio", Category.Audio);
                case "ogg": return new FileKind(id, "Ogg audio", Category.Audio);
                case "mp4": return new FileKind(id, "MP4 video", Category.Video);
                case "m4v": return new FileKind(id, "M4V video", Category.Video);
                case "mov": return new FileKind(id, "QuickTime video", Category.Video);
                case "3gp": return new FileKind(id, "3GP video", Category.Video);
                case "avi": return new FileKind(id, "AVI video", Category.Video);
                case "wmv": return new FileKind(id, "WMV video", Category.Video);
                case "mkv": return new FileKind(id, "MKV video", Category.Video);
                case "webm": return new FileKind(id, "WebM video", Category.Video);
                case "pdf": return new FileKind(id, "PDF document", Category.Pdf);
                case "docx": return new FileKind(id, "Word document", Category.Document);
                case "odt": return new FileKind(id, "OpenDocument text", Category.Document);
                case "rtf": return new FileKind(id, "Rich Text document", Category.Document);
                case "txt": return new FileKind(id, "Plain text", Category.Document);
                case "md": return new FileKind(id, "Markdown", Category.Document);
                case "html": return new FileKind(id, "HTML page", Category.Document);
                case "epub": return new FileKind(id, "EPUB e-book", Category.Document);
                case "pptx": return new FileKind(id, "PowerPoint presentation", Category.Document);
                case "srt": return new FileKind(id, "SRT subtitles", Category.Document);
                case "vtt": return new FileKind(id, "WebVTT subtitles", Category.Document);
                case "xlsx": return new FileKind(id, "Excel spreadsheet", Category.Data);
                case "ods": return new FileKind(id, "OpenDocument spreadsheet", Category.Data);
                case "csv": return new FileKind(id, "CSV table", Category.Data);
                case "tsv": return new FileKind(id, "TSV table", Category.Data);
                case "json": return new FileKind(id, "JSON data", Category.Data);
                case "xml": return new FileKind(id, "XML data", Category.Data);
                case "yaml": return new FileKind(id, "YAML data", Category.Data);
                case "zip": return new FileKind(id, "ZIP archive", Category.Archive);
                case "tar": return new FileKind(id, "TAR archive", Category.Archive);
                case "gz": return new FileKind(id, "GZip file", Category.Archive);
                case "tgz": return new FileKind(id, "TAR.GZ archive", Category.Archive);
                case "7z": return new FileKind(id, "7-Zip archive", Category.Archive);
                case "rar": return new FileKind(id, "RAR archive", Category.Archive);
                default: return new FileKind(id, id.ToUpperInvariant() + " file", Category.Other);
            }
        }
    }
}
