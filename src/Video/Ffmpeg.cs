using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Konbini.Video
{
    public class ProbeInfo
    {
        public double Duration;     // seconds
        public int Width, Height;
        public double Fps;
        public long VideoKbps, AudioKbps, TotalKbps;
        public bool HasVideo, HasAudio;
        public string AudioCodec, VideoCodec;
        public int Rotation;        // 90/270 means the displayed frame is portrait
        public int DisplayWidth { get { return Rotation % 180 != 0 ? Height : Width; } }
        public int DisplayHeight { get { return Rotation % 180 != 0 ? Width : Height; } }
    }

    // Runs the ffmpeg.exe embedded in Konbini. It is unpacked once per user into %LOCALAPPDATA%\Konbini\bin
    // and verified against the SHA-256 baked in at build time (FfmpegHash.Sha256) before every first use.
    public static class Ffmpeg
    {
        private static readonly object unpackLock = new object();
        public static Konbini.Deps.Dependency Dep { get { return Konbini.Deps.Registry.Ffmpeg; } }

        /// <summary>True when the verified video engine is installed (it's downloaded on first use).</summary>
        public static bool Available { get { return Konbini.Deps.DepManager.IsInstalled(Dep); } }

        public static string ExePath { get { return Konbini.Deps.DepManager.InstallPath(Dep); } }

        // Windows command-line quoting (CommandLineToArgvW rules).
        public static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', bs); sb.Append(c); }
                bs = 0;
            }
            sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }

        // ---- Job object: ffmpeg is killed automatically if Konbini exits or crashes. ----
        private static IntPtr job = IntPtr.Zero;
        private static void AddToJob(Process p)
        {
            try
            {
                lock (unpackLock)
                {
                    if (job == IntPtr.Zero)
                    {
                        IntPtr h = CreateJobObject(IntPtr.Zero, null);
                        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        int len = Marshal.SizeOf(info);
                        IntPtr ptr = Marshal.AllocHGlobal(len);
                        try { Marshal.StructureToPtr(info, ptr, false); SetInformationJobObject(h, 9, ptr, (uint)len); }
                        finally { Marshal.FreeHGlobal(ptr); }
                        job = h;
                    }
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch { }
        }

        private static readonly List<Process> running = new List<Process>();
        public static int RunningCount { get { lock (running) return running.Count; } }

        // Runs ffmpeg. progress gets 0..100 based on out_time vs duration. Throws OperationCanceledException on cancel.
        public static void Run(IList<string> args, double duration, Action<int> progress, Func<bool> cancelled)
        {
            var sbArgs = new StringBuilder("-hide_banner -nostdin -nostats -loglevel error -progress pipe:1");
            foreach (var a in args) sbArgs.Append(' ').Append(Quote(a));
            using (Konbini.Deps.DepManager.Acquire(Dep))   // verified, and locked against replacement while it runs
            {
            var psi = new ProcessStartInfo(ExePath, sbArgs.ToString())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            var err = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) { err.AppendLine(e.Data); if (err.Length > 4096) err.Remove(0, err.Length - 4096); } };
                int last = -1;
                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null || progress == null || duration <= 0) return;
                    if (e.Data.StartsWith("out_time_us=") || e.Data.StartsWith("out_time_ms="))
                    {
                        long us;
                        if (long.TryParse(e.Data.Substring(12), out us) && us > 0)
                        {
                            int pc = (int)Math.Max(0, Math.Min(99, us / 1e6 / duration * 100));
                            if (pc != last) { last = pc; progress(pc); }
                        }
                    }
                };
                p.Start();
                AddToJob(p);
                lock (running) running.Add(p);
                try
                {
                    p.BeginErrorReadLine(); p.BeginOutputReadLine();
                    while (!p.WaitForExit(150))
                    {
                        if (cancelled != null && cancelled())
                        {
                            try { p.Kill(); } catch { }
                            p.WaitForExit(5000);
                            throw new OperationCanceledException();
                        }
                    }
                    p.WaitForExit();
                }
                finally { lock (running) running.Remove(p); }
                if (p.ExitCode != 0) throw new InvalidOperationException(Friendly(err.ToString()));
            }
            }
        }

        private static string Friendly(string err)
        {
            string e = err.Trim();
            if (e.IndexOf("No space left", StringComparison.OrdinalIgnoreCase) >= 0) return "The disk is full.";
            if (e.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0) return "Konbini isn't allowed to write to that folder.";
            if (e.IndexOf("Invalid data found", StringComparison.OrdinalIgnoreCase) >= 0 || e.IndexOf("moov atom not found", StringComparison.OrdinalIgnoreCase) >= 0)
                return "This file isn't a readable video (it may be damaged or incomplete).";
            if (e.IndexOf("does not contain any stream", StringComparison.OrdinalIgnoreCase) >= 0) return "This file has no video or audio in it.";
            var lines = e.Split('\n');
            string lastLine = lines.Length > 0 ? lines[lines.Length - 1].Trim() : "";
            return "The video couldn't be compressed" + (lastLine.Length > 0 ? ": " + (lastLine.Length > 200 ? lastLine.Substring(0, 200) : lastLine) : ".");
        }

        // Reads stream info from `ffmpeg -i` (ffprobe isn't shipped).
        public static ProbeInfo Probe(string path)
        {
            var psi = new ProcessStartInfo(ExePath, "-hide_banner -nostdin -i " + Quote(Path.GetFullPath(path)))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            string text;
            using (Konbini.Deps.DepManager.Acquire(Dep))
            using (var p = Process.Start(psi))
            {
                AddToJob(p);
                var so = p.StandardOutput.ReadToEndAsync();
                text = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } }
                so.Wait(1000);
            }
            var info = new ProbeInfo();
            var inv = CultureInfo.InvariantCulture;
            var m = Regex.Match(text, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
            if (m.Success) info.Duration = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, inv);
            m = Regex.Match(text, @"bitrate: (\d+) kb/s");
            if (m.Success) info.TotalKbps = long.Parse(m.Groups[1].Value);
            foreach (Match s in Regex.Matches(text, @"Stream #\d+:\d+[^:]*: (Video|Audio): ([^\r\n]+)"))
            {
                string line = s.Groups[2].Value;
                string codec = line.Split(new[] { ' ', ',' }, 2)[0];
                var br = Regex.Match(line, @"(\d+) kb/s");
                if (s.Groups[1].Value == "Video" && !info.HasVideo)
                {
                    if (line.Contains("(attached pic)")) continue;
                    info.HasVideo = true; info.VideoCodec = codec;
                    var wh = Regex.Match(line, @", (\d{2,5})x(\d{2,5})");
                    if (wh.Success) { info.Width = int.Parse(wh.Groups[1].Value); info.Height = int.Parse(wh.Groups[2].Value); }
                    var fps = Regex.Match(line, @"([\d.]+) fps");
                    if (fps.Success) double.TryParse(fps.Groups[1].Value, NumberStyles.Float, inv, out info.Fps);
                    if (br.Success) info.VideoKbps = long.Parse(br.Groups[1].Value);
                }
                else if (s.Groups[1].Value == "Audio" && !info.HasAudio)
                {
                    info.HasAudio = true; info.AudioCodec = codec;
                    if (br.Success) info.AudioKbps = long.Parse(br.Groups[1].Value);
                }
            }
            m = Regex.Match(text, @"rotation of (-?[\d.]+) degrees");
            if (!m.Success) m = Regex.Match(text, @"rotate\s*:\s*(-?\d+)");
            if (m.Success) { double r; if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, inv, out r)) info.Rotation = (((int)Math.Round(r)) % 360 + 360) % 360; }
            if (!info.HasVideo && !info.HasAudio)
                throw new InvalidOperationException(text.IndexOf("Invalid data", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("moov atom", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "This file isn't a readable video (it may be damaged or incomplete)." : "This file has no video in it.");
            return info;
        }

        // One frame as a bitmap (for thumbnails). Returns null if it can't be read.
        public static System.Drawing.Bitmap Frame(string path, double at, int box)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "konbini-thumb-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var args = new[] { "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", Path.GetFullPath(path), "-frames:v", "1",
                    "-vf", "scale=" + box + ":" + box + ":force_original_aspect_ratio=decrease", "-y", tmp };
                Run(args, 0, null, null);
                if (!File.Exists(tmp)) return null;
                using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read))
                using (var img = System.Drawing.Image.FromStream(fs)) return new System.Drawing.Bitmap(img);
            }
            catch { return null; }
            finally { try { File.Delete(tmp); } catch { } }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
            public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }
}
