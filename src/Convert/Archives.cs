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
        // Entries are streamed one at a time from the source archive to the new one, so memory use stays flat no
        // matter how big the archive is (both ZIP and TAR record each entry's size up front).
        private delegate void EntrySink(string name, bool isDir, DateTime modified, long size, Stream data);

        /// <summary>Converts between archive formats; returns the number of files copied.</summary>
        public static int Transcode(string src, string srcId, string dst, string dstId)
        {
            int files = 0;
            switch (dstId)
            {
                case "zip":
                    if (File.Exists(dst)) File.Delete(dst);
                    using (var z = ZipFile.Open(dst, ZipArchiveMode.Create))
                        ReadEntries(src, srcId, (name, isDir, modified, size, data) =>
                        {
                            var ze = z.CreateEntry(isDir ? name.TrimEnd('/') + "/" : name, CompressionLevel.Optimal);
                            ze.LastWriteTime = modified;
                            if (!isDir) { using (var o = ze.Open()) CopyN(data, o, size); files++; }
                        });
                    break;
                case "tar": using (var fs = File.Create(dst)) files = WriteTar(src, srcId, fs); break;
                case "tgz": using (var fs = File.Create(dst)) using (var gz = new GZipStream(fs, CompressionLevel.Optimal)) files = WriteTar(src, srcId, gz); break;
                default: throw new NotSupportedException("Can't create ." + dstId + " archives.");
            }
            return files;
        }

        private static void ReadEntries(string path, string id, EntrySink sink)
        {
            switch (id)
            {
                case "zip":
                    using (var z = ZipFile.OpenRead(path))
                        foreach (var e in z.Entries)
                        {
                            bool dir = e.FullName.EndsWith("/");
                            if (dir) sink(e.FullName, true, e.LastWriteTime.DateTime, 0, Stream.Null);
                            else using (var s = e.Open()) sink(e.FullName, false, e.LastWriteTime.DateTime, e.Length, s);
                        }
                    break;
                case "tar": using (var fs = File.OpenRead(path)) ReadTar(fs, sink); break;
                case "tgz": using (var fs = File.OpenRead(path)) using (var gz = new GZipStream(fs, CompressionMode.Decompress)) ReadTar(gz, sink); break;
                default: throw new NotSupportedException("Can't open ." + id + " archives.");
            }
        }

        private static void ReadTar(Stream s, EntrySink sink)
        {
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
                int pad = (int)((512 - size % 512) % 512);
                if (type == 'L')
                {
                    if (size > 1 << 20) throw new InvalidDataException("The TAR file has an invalid long file name.");
                    var nb = new byte[size];
                    if (ReadFull(s, nb, (int)size) != size) throw new InvalidDataException("The TAR file is truncated.");
                    longName = Encoding.UTF8.GetString(nb).TrimEnd('\0');
                }
                else
                {
                    if (longName != null) { name = longName; longName = null; }
                    var body = new Limited(s, size);
                    if (type == '0' || type == '\0' || type == '5')
                        sink(name, type == '5' || name.EndsWith("/"),
                             new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(mtime).ToLocalTime(), size, body);
                    body.Drain();
                    if (body.Remaining > 0) throw new InvalidDataException("The TAR file is truncated.");
                }
                if (pad > 0 && ReadFull(s, new byte[pad], pad) != pad) break;
            }
        }

        private static int WriteTar(string src, string srcId, Stream s)
        {
            int files = 0;
            ReadEntries(src, srcId, (rawName, isDir, modified, size, data) =>
            {
                string name = rawName.Replace('\\', '/');
                if (isDir && !name.EndsWith("/")) name += "/";
                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                if (nameBytes.Length > 100)
                {
                    // GNU long-name record precedes the real header.
                    WriteHeader(s, "././@LongLink", nameBytes.Length + 1, 'L', DateTime.Now);
                    s.Write(nameBytes, 0, nameBytes.Length); s.WriteByte(0);
                    Pad(s, nameBytes.Length + 1);
                }
                long len = isDir ? 0 : size;
                WriteHeader(s, name, len, isDir ? '5' : '0', modified);
                if (!isDir) { CopyN(data, s, len); Pad(s, len); files++; }
            });
            s.Write(new byte[1024], 0, 1024);
            return files;
        }

        // Copies exactly n bytes (the size the header promised); a short source means a corrupt archive.
        private static void CopyN(Stream from, Stream to, long n)
        {
            var buf = new byte[81920];
            while (n > 0)
            {
                int r = from.Read(buf, 0, (int)Math.Min(buf.Length, n));
                if (r <= 0) throw new InvalidDataException("The archive is truncated or damaged.");
                to.Write(buf, 0, r); n -= r;
            }
        }

        // Read-only view of the next n bytes of a sequential stream (one TAR entry's data).
        private sealed class Limited : Stream
        {
            private readonly Stream s; public long Remaining;
            public Limited(Stream s, long n) { this.s = s; Remaining = n; }
            public override int Read(byte[] b, int o, int c)
            {
                if (Remaining <= 0) return 0;
                int r = s.Read(b, o, (int)Math.Min(c, Remaining)); if (r > 0) Remaining -= r; return r;
            }
            public void Drain() { var b = new byte[81920]; while (Remaining > 0 && Read(b, 0, b.Length) > 0) { } }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); }
            public override void SetLength(long v) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
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
