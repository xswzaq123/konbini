using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace Konbini.Conversion
{
    public class Target
    {
        public readonly string Id, Label;
        public Target(string id, string label) { Id = id; Label = label; }
        public override string ToString() { return Label; }
    }

    public class ConvertOptions
    {
        public int Quality;        // 0 high, 1 medium, 2 low
        public int JpegQuality = 90;
    }

    public static class Converters
    {
        private static readonly Lazy<bool> heif = new Lazy<bool>(ImageConv.CanEncodeHeif);

        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            { "jpg", "JPG" }, { "png", "PNG" }, { "gif", "GIF" }, { "bmp", "BMP" }, { "tif", "TIFF" }, { "ico", "ICO (icon)" },
            { "jxr", "JPEG XR" }, { "heic", "HEIC" }, { "pdf", "PDF" },
            { "mp3", "MP3" }, { "m4a", "M4A (AAC)" }, { "wav", "WAV" }, { "wma", "WMA" }, { "flac", "FLAC" },
            { "mp4", "MP4 (H.264)" }, { "wmv", "WMV" }, { "avi", "AVI" },
            { "txt", "TXT" }, { "md", "Markdown" }, { "html", "HTML" }, { "rtf", "RTF" }, { "docx", "DOCX (Word)" },
            { "csv", "CSV" }, { "tsv", "TSV" }, { "xlsx", "XLSX (Excel)" }, { "json", "JSON" }, { "xml", "XML" }, { "yaml", "YAML" },
            { "srt", "SRT" }, { "vtt", "VTT" },
            { "zip", "ZIP" }, { "tar", "TAR" }, { "tgz", "TAR.GZ" }, { "gz", "GZ" }, { "ungz", "Extract (un-gzip)" },
        };

        public static string ExtensionFor(string target)
        {
            if (target == "tgz") return ".tar.gz";
            if (target == "ungz") return "";
            return "." + target;
        }

        private static List<Target> T(FileKind k, params string[] ids)
        {
            return ids.Where(i => i != k.Id).Distinct().Select(i => new Target(i, Labels.ContainsKey(i) ? Labels[i] : i.ToUpperInvariant())).ToList();
        }

        public static List<Target> TargetsFor(FileKind k)
        {
            var list = new List<Target>();
            switch (k.Category)
            {
                case Category.Image:
                    list = T(k, "jpg", "png", "gif", "bmp", "tif", "ico", "pdf", "jxr");
                    if (heif.Value) list.AddRange(T(k, "heic"));
                    break;
                case Category.Audio: list = T(k, MediaConv.AudioTargets); break;
                case Category.Video: list = T(k, MediaConv.VideoTargets.Concat(MediaConv.AudioTargets).ToArray()); break;
                case Category.Pdf: list = T(k, "png", "jpg", "tif", "bmp", "gif"); break;
                case Category.Document:
                    switch (k.Id)
                    {
                        case "srt": list = T(k, "vtt", "txt"); break;
                        case "vtt": list = T(k, "srt", "txt"); break;
                        case "svg": break;
                        case "pptx": case "epub": list = T(k, "txt", "md", "html", "pdf", "docx"); break;
                        default: list = T(k, "pdf", "docx", "html", "txt", "md", "rtf"); break;
                    }
                    break;
                case Category.Data:
                    switch (k.Id)
                    {
                        case "json": list = T(k, "csv", "xlsx", "xml", "yaml", "html", "tsv"); break;
                        case "xml": list = T(k, "json", "yaml", "csv", "xlsx"); break;
                        case "yaml": break;
                        default: list = T(k, "xlsx", "csv", "json", "xml", "tsv", "html", "md", "pdf"); break;
                    }
                    break;
                case Category.Archive:
                    switch (k.Id)
                    {
                        case "zip": list = T(k, "tgz", "tar"); break;
                        case "tar": list = T(k, "zip", "tgz"); break;
                        case "tgz": list = T(k, "zip", "tar"); break;
                        case "gz": list = T(k, "ungz"); break;
                    }
                    break;
            }
            // Any file can be compressed.
            if (k.Category != Category.Archive || k.Id == "7z" || k.Id == "rar") list.AddRange(T(k, "zip", "gz"));
            else if (k.Id != "gz" && k.Id != "tgz") list.AddRange(T(k, "gz"));
            return list.GroupBy(t => t.Id).Select(g => g.First()).ToList();
        }

        // Converts one file and returns a short result description for the UI.
        public static string Convert(string src, FileKind k, string target, string baseOut, ConvertOptions o, Func<string, string> reserve)
        {
            long before = new FileInfo(src).Length;
            var outputs = new List<string>();
            string note = "";
            Func<string> outPath = () =>
            {
                string p = baseOut + ExtensionFor(target);
                // Never overwrite the source file.
                if (string.Equals(Path.GetFullPath(p), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase)) p = baseOut + " (converted)" + ExtensionFor(target);
                p = reserve(p);
                outputs.Add(p);
                return p;
            };

            if (target == "zip" && k.Category != Category.Archive || target == "zip" && (k.Id == "7z" || k.Id == "rar")) Archives.ZipSingle(src, outPath());
            else if (target == "gz") Archives.Gzip(src, outPath());
            else if (target == "ungz")
            {
                string name = Path.GetFileName(src);
                name = name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 3) : name + ".out";
                string p = reserve(Path.Combine(Path.GetDirectoryName(baseOut), name));
                outputs.Add(p);
                Archives.Gunzip(src, p);
            }
            else switch (k.Category)
            {
                case Category.Image:
                    using (var bmp = ImageConv.Load(src, k.Id)) ImageConv.Save(bmp, outPath(), target, o.JpegQuality);
                    break;
                case Category.Audio:
                case Category.Video:
                    MediaConv.Transcode(src, outPath(), target, o.Quality, k.Category == Category.Video);
                    break;
                case Category.Pdf:
                    outputs.AddRange(PdfConv.ToImages(src, target, baseOut, o.JpegQuality, reserve));
                    break;
                case Category.Document:
                    if (k.Id == "srt" || k.Id == "vtt")
                    {
                        var cues = Subtitles.Read(src, k.Id);
                        if (target == "txt") File.WriteAllText(outPath(), string.Join("\n\n", cues.Select(c => c.Text)), new UTF8Encoding(false));
                        else Subtitles.Write(cues, outPath(), target);
                    }
                    else DocWriters.Write(DocReaders.Read(src, k.Id), outPath(), target);
                    note = target == "pdf" || target == "docx" || target == "rtf" ? "  · text & structure kept, styling simplified" : "";
                    break;
                case Category.Data:
                    if (k.Id == "json" && (target == "xml" || target == "yaml"))
                    {
                        var tree = Json.Parse(DocReaders.ReadText(src));
                        if (target == "xml") Json.ToXml(tree).Save(outPath());
                        else File.WriteAllText(outPath(), Json.ToYaml(tree), new UTF8Encoding(false));
                    }
                    else if (k.Id == "xml" && (target == "json" || target == "yaml"))
                    {
                        var x = System.Xml.Linq.XDocument.Load(src);
                        var tree = new Dictionary<string, object> { { x.Root.Name.LocalName, Json.FromXElement(x.Root) } };
                        File.WriteAllText(outPath(), target == "json" ? Json.Pretty(tree) : Json.ToYaml(tree), new UTF8Encoding(false));
                    }
                    else
                    {
                        string tnote;
                        var rows = Tables.Read(src, k.Id, out tnote);
                        Tables.Write(rows, outPath(), target);
                        note = "  · " + Math.Max(0, rows.Count - 1) + " rows" + tnote;
                    }
                    break;
                case Category.Archive:
                    var entries = Archives.Read(src, k.Id);
                    Archives.Write(entries, outPath(), target);
                    note = "  · " + entries.Count(e => !e.IsDir) + " files";
                    break;
                default:
                    throw new NotSupportedException("No converter for this file type.");
            }

            long after = outputs.Where(File.Exists).Sum(p => new FileInfo(p).Length);
            string first = Path.GetFileName(outputs.FirstOrDefault() ?? "");
            string what = outputs.Count > 1 ? outputs.Count + " files (" + first + " …)" : first;
            return "✓ " + what + "  ·  " + ImageUtil.FormatBytes(before) + " → " + ImageUtil.FormatBytes(after) + note;
        }
    }
}
