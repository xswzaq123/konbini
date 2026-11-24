using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Konbini.Video
{
    public enum VCodec { H264, H265 }

    public class CompressOptions
    {
        public VCodec Codec = VCodec.H264;
        public int Crf = 23;              // used when TargetMB <= 0
        public double TargetMB = 0;       // > 0 = two-pass to fit this size
        public string Preset = "medium";
        public int MaxHeight = 0;         // 0 = keep (for portrait video this limits the short side)
        public int MaxWidth = 0;          // 0 = keep
        public double MaxFps = 0;         // 0 = keep
        public int AudioKbps = 128;       // 0 = remove audio, -1 = copy unchanged
        public bool AutoDownscaleForTarget = false; // drop to 480p when the target bitrate is very low

        public CompressOptions Clone() { return (CompressOptions)MemberwiseClone(); }
    }

    public class Template
    {
        public string Name, Hint;
        public CompressOptions Options;
        public Template(string name, string hint, CompressOptions o) { Name = name; Hint = hint; Options = o; }
        public override string ToString() { return Name; }
    }

    public static class VideoCompress
    {
        public static readonly Template[] Templates =
        {
            new Template("Balanced (recommended)", "H.264, good quality at about a third of the size. Plays everywhere.",
                new CompressOptions { Codec = VCodec.H264, Crf = 23, Preset = "medium", AudioKbps = 128 }),
            new Template("Online Sharing", "H.265 CRF 32, slow preset, AAC 96k — smallest files for uploading.",
                new CompressOptions { Codec = VCodec.H265, Crf = 32, Preset = "slow", AudioKbps = 96 }),
            new Template("Messaging (plays everywhere)", "H.264, up to 720p and 30 fps, AAC 96k — WhatsApp, Telegram, iMessage, old PCs.",
                new CompressOptions { Codec = VCodec.H264, Crf = 26, Preset = "medium", MaxHeight = 720, MaxFps = 30, AudioKbps = 96 }),
            new Template("Reduced Resolution", "Scales to 1280 px wide, H.264 CRF 28, AAC 128k.",
                new CompressOptions { Codec = VCodec.H264, Crf = 28, Preset = "medium", MaxWidth = 1280, AudioKbps = 128 }),
            new Template("Better Quality", "H.265 CRF 24, medium preset, AAC 128k — visibly clean, still much smaller.",
                new CompressOptions { Codec = VCodec.H265, Crf = 24, Preset = "medium", AudioKbps = 128 }),
            new Template("Fit under 10 MB", "Two-pass H.264 sized to fit 10 MB (Discord). Up to 720p, 480p for long clips.",
                new CompressOptions { Codec = VCodec.H264, TargetMB = 9.5, Preset = "medium", MaxHeight = 720, AudioKbps = 64, AutoDownscaleForTarget = true }),
            new Template("Fit under 25 MB", "Two-pass H.264 sized to fit 25 MB (email). Up to 1080p.",
                new CompressOptions { Codec = VCodec.H264, TargetMB = 24, Preset = "medium", MaxHeight = 1080, AudioKbps = 96, AutoDownscaleForTarget = true }),
            new Template("Archive (near-lossless)", "H.265 CRF 20, slow preset, original audio kept — keep quality, save space.",
                new CompressOptions { Codec = VCodec.H265, Crf = 20, Preset = "slow", AudioKbps = -1 }),
        };

        public const double MinWatchableKbps = 150;

        // Video bitrate (kbps) needed to hit opts.TargetMB for a clip of this length.
        public static double TargetVideoKbps(CompressOptions o, ProbeInfo info)
        {
            double audio = !info.HasAudio || o.AudioKbps == 0 ? 0 : o.AudioKbps > 0 ? o.AudioKbps : Math.Max(info.AudioKbps, 128);
            double totalKbps = o.TargetMB * 8388.608 / Math.Max(0.1, info.Duration) * 0.97; // 3% container overhead
            return totalKbps - audio;
        }

        public static List<string> BuildArgs(string src, string dst, CompressOptions o, ProbeInfo info, int pass, string passLog, out string warning)
        {
            warning = null;
            var inv = CultureInfo.InvariantCulture;
            var a = new List<string> { "-i", Path.GetFullPath(src), "-map", "0:v:0" };
            bool audio = info.HasAudio && o.AudioKbps != 0 && pass != 1;
            if (audio) a.AddRange(new[] { "-map", "0:a:0?" });
            a.AddRange(new[] { "-map_metadata", "0", "-sn", "-dn" });

            // Scale (never up) and fps cap.
            int maxH = o.MaxHeight, maxW = o.MaxWidth;
            double vk = 0;
            if (o.TargetMB > 0)
            {
                vk = TargetVideoKbps(o, info);
                if (o.AutoDownscaleForTarget && vk < 500 && (maxH == 0 || maxH > 480)) maxH = 480;
                if (vk < MinWatchableKbps) warning = "too small for this length — quality will be very low";
                vk = Math.Max(80, vk);
            }
            var filters = new List<string>();
            int w = info.DisplayWidth, h = info.DisplayHeight;
            int shortSide = Math.Min(w, h);
            if (maxH > 0 && shortSide > maxH)
                filters.Add(w >= h ? "scale=-2:" + maxH : "scale=" + maxH + ":-2");
            else if (maxW > 0 && w > maxW)
                filters.Add("scale=" + maxW + ":-2");
            else if (w % 2 != 0 || h % 2 != 0)
                filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
            if (o.MaxFps > 0 && info.Fps > o.MaxFps + 0.5) filters.Add("fps=" + o.MaxFps.ToString(inv));
            filters.Add("format=yuv420p");
            a.AddRange(new[] { "-vf", string.Join(",", filters) });

            if (o.Codec == VCodec.H265)
                a.AddRange(new[] { "-c:v", "libx265", "-tag:v", "hvc1", "-x265-params", "log-level=error" + (pass > 0 ? ":pass=" + pass + ":stats=" + passLog.Replace("\\", "/").Replace(":", "\\:") : "") });
            else
                a.AddRange(new[] { "-c:v", "libx264" });
            a.AddRange(new[] { "-preset", o.Preset });
            if (o.TargetMB > 0)
            {
                string kb = ((int)vk).ToString(inv) + "k";
                a.AddRange(new[] { "-b:v", kb, "-maxrate", ((int)(vk * 1.5)).ToString(inv) + "k", "-bufsize", ((int)(vk * 2)).ToString(inv) + "k" });
                if (o.Codec == VCodec.H264 && pass > 0) a.AddRange(new[] { "-pass", pass.ToString(inv), "-passlogfile", passLog });
            }
            else a.AddRange(new[] { "-crf", o.Crf.ToString(inv) });

            if (pass == 1) { a.AddRange(new[] { "-an", "-f", "mp4", "-y", "NUL" }); return a; }
            if (!audio) a.Add("-an");
            else if (o.AudioKbps < 0 && (info.AudioCodec == "aac" || info.AudioCodec == "mp3")) a.AddRange(new[] { "-c:a", "copy" });
            else if (o.AudioKbps < 0) a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
            else if (info.AudioCodec == "aac" && info.AudioKbps > 0 && info.AudioKbps <= o.AudioKbps + 8) a.AddRange(new[] { "-c:a", "copy" });
            else a.AddRange(new[] { "-c:a", "aac", "-b:a", o.AudioKbps + "k" });
            a.AddRange(new[] { "-movflags", "+faststart", "-n", Path.GetFullPath(dst) });
            return a;
        }

        public enum Outcome { Compressed, KeptOriginal }

        // Compresses src into dst (dst must not exist). Returns KeptOriginal (and deletes dst) when the result isn't smaller.
        public static Outcome Compress(string src, string dst, CompressOptions o, Action<int> progress, Func<bool> cancelled, out string warning)
        {
            var info = Ffmpeg.Probe(src);
            if (!info.HasVideo) throw new InvalidOperationException("This file has no video in it.");
            if (info.Duration <= 0 && o.TargetMB > 0) throw new InvalidOperationException("Can't tell how long this video is, so it can't be sized to a target. Use a quality template instead.");
            string tmpDir = null;
            try
            {
                if (o.TargetMB > 0)
                {
                    tmpDir = Path.Combine(Path.GetTempPath(), "konbini-2pass-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tmpDir);
                    string log = Path.Combine(tmpDir, "pass");
                    string w;
                    Ffmpeg.Run(BuildArgs(src, dst, o, info, 1, log, out w), info.Duration, p => { if (progress != null) progress(p / 2); }, cancelled);
                    Ffmpeg.Run(BuildArgs(src, dst, o, info, 2, log, out warning), info.Duration, p => { if (progress != null) progress(50 + p / 2); }, cancelled);
                }
                else Ffmpeg.Run(BuildArgs(src, dst, o, info, 0, null, out warning), info.Duration, progress, cancelled);
            }
            catch { try { if (File.Exists(dst)) File.Delete(dst); } catch { } throw; }
            finally { if (tmpDir != null) try { Directory.Delete(tmpDir, true); } catch { } }
            if (!File.Exists(dst)) throw new InvalidOperationException("The video engine finished but produced no file.");
            if (o.TargetMB <= 0 && new FileInfo(dst).Length >= new FileInfo(src).Length)
            {
                File.Delete(dst);
                return Outcome.KeptOriginal;
            }
            return Outcome.Compressed;
        }

        public static string FormatDuration(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Round(s));
            return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") : t.Minutes + ":" + t.Seconds.ToString("00");
        }
    }
}
