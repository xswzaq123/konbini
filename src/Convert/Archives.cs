using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Konbini.Conversion
{
    // ZIP / TAR / TAR.GZ / GZ using only the built-in compression streams.
    public static class Archives
    {
        public class Entry { public string Name; public byte[] Data; public bool IsDir; public DateTime Modified; }

        public static List<Entry> Read(string path, string id)
        {
            switch (id)
            {
                case "zip": return ReadZip(path);
                case "tar": using (var fs = File.OpenRead(path)) return ReadTar(fs);
                case "tgz": using (var fs = File.OpenRead(path)) using (var gz = new GZipStream(fs, CompressionMode.Decompress)) return ReadTar(gz);
                default: throw new NotSupportedException("Can't open ." + id + " archives.");
            }
        }

        public static void Write(List<Entry> entries, string path, string id)
        {
            switch (id)
            {
                case "zip":
                    if (File.Exists(path)) File.Delete(path);
                    using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
                        foreach (var e in entries)
                        {
                            var ze = z.CreateEntry(e.IsDir ? e.Name.TrimEnd('/') + "/" : e.Name, CompressionLevel.Optimal);
                            ze.LastWriteTime = e.Modified;
                            if (!e.IsDir) using (var s = ze.Open()) s.Write(e.Data, 0, e.Data.Length);
                        }
                    break;
                case "tar": using (var fs = File.Create(path)) WriteTar(entries, fs); break;
                case "tgz": using (var fs = File.Create(path)) using (var gz = new GZipStream(fs, CompressionLevel.Optimal)) WriteTar(entries, gz); break;
                default: throw new NotSupportedException("Can't create ." + id + " archives.");
            }
        }

        private static List<Entry> ReadZip(string path)
        {
            var list = new List<Entry>();
            using (var z = ZipFile.OpenRead(path))
                foreach (var e in z.Entries)
                {
                    bool dir = e.FullName.EndsWith("/");
                    byte[] data = new byte[0];
                    if (!dir) using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
                    list.Add(new Entry { Name = e.FullName, Data = data, IsDir = dir, Modified = e.LastWriteTime.DateTime });
                }
            return list;
        }

        private static List<Entry> ReadTar(Stream s)
        {
            var list = new List<Entry>();
            var header = new byte[512];
            string longName = null;
            while (ReadFull(s, header, 512) == 512)
            {
                if (IsZero(header)) break;
                string name = Str(header, 0, 100), prefix = Str(header, 345, 155);
                if (prefix.Length > 0 && Encoding.ASCII.GetString(header, 257, 5) == "ustar") name = prefix + "/" + name;
                long size = Octal(header, 124, 12);
                long mtime = Octal(header, 136, 12);
                char type = (char)header[156];
                var data = new byte[size];
                if (ReadFull(s, data, (int)size) != size) throw new InvalidDataException("The TAR file is truncated.");
                int pad = (int)((512 - size % 512) % 512);
                if (pad > 0) ReadFull(s, new byte[pad], pad);
                if (type == 'L') { longName = Encoding.UTF8.GetString(data).TrimEnd('\0'); continue; }
                if (longName != null) { name = longName; longName = null; }
                if (type == '0' || type == '\0' || type == '5')
                    list.Add(new Entry { Name = name, Data = data, IsDir = type == '5' || name.EndsWith("/"),
                        Modified = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(mtime).ToLocalTime() });
            }
            return list;
        }

        private static void WriteTar(List<Entry> entries, Stream s)
        {
            foreach (var e in entries)
            {
                string name = e.Name.Replace('\\', '/');
                if (e.IsDir && !name.EndsWith("/")) name += "/";
                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                if (nameBytes.Length > 100)
                {
                    // GNU long-name record precedes the real header.
                    WriteHeader(s, "././@LongLink", nameBytes.Length + 1, 'L', DateTime.Now);
                    s.Write(nameBytes, 0, nameBytes.Length); s.WriteByte(0);
                    Pad(s, nameBytes.Length + 1);
                }
                WriteHeader(s, name, e.IsDir ? 0 : e.Data.Length, e.IsDir ? '5' : '0', e.Modified);
                if (!e.IsDir) { s.Write(e.Data, 0, e.Data.Length); Pad(s, e.Data.Length); }
            }
            s.Write(new byte[1024], 0, 1024);
        }

        private static void WriteHeader(Stream s, string name, long size, char type, DateTime modified)
        {
            var h = new byte[512];
            var nb = Encoding.UTF8.GetBytes(name);
            Buffer.BlockCopy(nb, 0, h, 0, Math.Min(100, nb.Length));
            Put(h, 100, type == '5' ? "0000755" : "0000644");
            Put(h, 108, "0000000"); Put(h, 116, "0000000");
            Put(h, 124, Convert.ToString(size, 8).PadLeft(11, '0'));
            long secs = (long)Math.Max(0, (modified.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            Put(h, 136, Convert.ToString(secs, 8).PadLeft(11, '0'));
            for (int i = 148; i < 156; i++) h[i] = (byte)' ';
            h[156] = (byte)type;
            Put(h, 257, "ustar"); h[262] = 0; Put(h, 263, "00");
            int sum = 0; foreach (var b in h) sum += b;
            Put(h, 148, Convert.ToString(sum, 8).PadLeft(6, '0')); h[154] = 0; h[155] = (byte)' ';
            s.Write(h, 0, 512);
        }

        private static void Put(byte[] h, int off, string v) { var b = Encoding.ASCII.GetBytes(v); Buffer.BlockCopy(b, 0, h, off, b.Length); }
        private static void Pad(Stream s, long len) { int p = (int)((512 - len % 512) % 512); if (p > 0) s.Write(new byte[p], 0, p); }
        private static bool IsZero(byte[] b) { foreach (var x in b) if (x != 0) return false; return true; }
        private static string Str(byte[] b, int off, int len) { int n = 0; while (n < len && b[off + n] != 0) n++; return Encoding.UTF8.GetString(b, off, n); }

        private static long Octal(byte[] b, int off, int len)
        {
            string s = Str(b, off, len).Trim(' ', '\0');
            return s.Length == 0 ? 0 : Convert.ToInt64(s, 8);
        }

        private static int ReadFull(Stream s, byte[] buf, int count)
        {
            int total = 0;
            while (total < count) { int n = s.Read(buf, total, count - total); if (n <= 0) break; total += n; }
            return total;
        }

        // Single-file helpers
        public static void ZipSingle(string src, string dst)
        {
            if (File.Exists(dst)) File.Delete(dst);
            using (var z = ZipFile.Open(dst, ZipArchiveMode.Create)) z.CreateEntryFromFile(src, Path.GetFileName(src), CompressionLevel.Optimal);
        }

        public static void Gzip(string src, string dst)
        {
            using (var i = File.OpenRead(src)) using (var o = File.Create(dst)) using (var gz = new GZipStream(o, CompressionLevel.Optimal)) i.CopyTo(gz);
        }

        public static void Gunzip(string src, string dst)
        {
            using (var i = File.OpenRead(src)) using (var gz = new GZipStream(i, CompressionMode.Decompress)) using (var o = File.Create(dst)) gz.CopyTo(o);
        }
    }
}
