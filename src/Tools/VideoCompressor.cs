using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Konbini.Conversion;
using Konbini.Video;

namespace Konbini
{
    public class VideoCompressorTool : ITool
    {
        public string Name { get { return "Video Compressor"; } }
        public string Category { get { return "Video"; } }
        public string Description { get { return "Shrink videos for sharing, email or storage — quick templates or full control. Saves as MP4."; } }

        private static readonly int[] Heights = { 0, 2160, 1440, 1080, 720, 480, 360 };
        private static readonly double[] FpsCaps = { 0, 60, 30, 24 };
        private static readonly int[] AudioRates = { 128, 96, 64, -1, 0 };
        private static readonly string[] Presets = { "veryfast", "medium", "slow" };

        public Control CreateView()
        {
            var opts = new TableLayoutPanel { AutoSize = true, ColumnCount = 3 };
            Func<int, ComboBox> combo = w => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = w };

            var template = combo(230);
            foreach (var t in VideoCompress.Templates) template.Items.Add(t);
            template.Items.Add("Custom");
            var hint = Theme.MutedLabel(""); hint.MaximumSize = new Size(420, 0);

            var codec = combo(230); codec.Items.AddRange(new object[] { "H.264 (most compatible)", "H.265 (smaller files)" });
            var sizeMode = combo(230); sizeMode.Items.AddRange(new object[] { "Quality level (CRF)", "Fit to file size" });
            var crf = Ui.Num(14, 40, 23, 60);
            var crfHint = Theme.MutedLabel("lower = better, bigger"); crfHint.Margin = new Padding(0, 7, 0, 0);
            var crfRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
            crfRow.Controls.AddRange(new Control[] { crf, crfHint });
            var mb = Ui.Num(1, 100000, 25, 80);
            var mbLbl = Theme.MutedLabel("MB per video"); mbLbl.Margin = new Padding(0, 7, 0, 0);
            var mbRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
            mbRow.Controls.AddRange(new Control[] { mb, mbLbl });
            var valueCell = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
            valueCell.Controls.AddRange(new Control[] { crfRow, mbRow });

            var res = combo(230); res.Items.AddRange(new object[] { "Original", "4K (2160p)", "1440p", "1080p", "720p", "480p", "360p", "1280 px wide" });
            var fps = combo(230); fps.Items.AddRange(new object[] { "Original", "60 fps max", "30 fps max", "24 fps max" });
            var audio = combo(230); audio.Items.AddRange(new object[] { "AAC 128 kbps", "AAC 96 kbps", "AAC 64 kbps", "Keep original", "Remove audio" });
            var speed = combo(230); speed.Items.AddRange(new object[] { "Faster", "Balanced", "Smaller file (slow)" });
            var estimate = Theme.MutedLabel(""); estimate.MaximumSize = new Size(420, 0);

            int r = 0;
            opts.Controls.Add(Ui.L("Template:"), 0, r); opts.Controls.Add(template, 1, r++);
            opts.Controls.Add(hint, 1, r); opts.SetColumnSpan(hint, 2); r++;
            opts.Controls.Add(Ui.L("Codec:"), 0, r); opts.Controls.Add(codec, 1, r++);
            opts.Controls.Add(Ui.L("Size by:"), 0, r); opts.Controls.Add(sizeMode, 1, r); opts.Controls.Add(valueCell, 2, r++);
            opts.Controls.Add(Ui.L("Resolution:"), 0, r); opts.Controls.Add(res, 1, r++);
            opts.Controls.Add(Ui.L("Frame rate:"), 0, r); opts.Controls.Add(fps, 1, r++);
            opts.Controls.Add(Ui.L("Audio:"), 0, r); opts.Controls.Add(audio, 1, r++);
            opts.Controls.Add(Ui.L("Speed:"), 0, r); opts.Controls.Add(speed, 1, r++);
            opts.Controls.Add(estimate, 0, r); opts.SetColumnSpan(estimate, 3);

            var panel = new BatchImagePanel("Compress videos", opts) { Suffix = "_compressed", MaxWorkers = 1 };
            panel.Noun = "video"; panel.DetailsHeader = "Details";
            // e.g. _compressed-sharing, _compressed-under10mb; Custom: _compressed-h265-crf28-720p
            string[] slugs = { "balanced", "sharing", "messaging", "1280w", "quality", "under10mb", "under25mb", "archive" };
            string[] resSlugs = { "", "2160p", "1440p", "1080p", "720p", "480p", "360p", "1280w" };
            panel.AutoSuffix = () =>
            {
                int t = template.SelectedIndex;
                if (t >= 0 && t < VideoCompress.Templates.Length && t < slugs.Length) return "_compressed-" + slugs[t];
                string s = "_compressed-" + (codec.SelectedIndex == 1 ? "h265" : "h264")
                    + (sizeMode.SelectedIndex == 1 ? "-" + mb.Value + "mb" : "-crf" + crf.Value);
                if (res.SelectedIndex > 0 && res.SelectedIndex < resSlugs.Length) s += "-" + resSlugs[res.SelectedIndex];
                return s;
            };
            var infos = new System.Collections.Concurrent.ConcurrentDictionary<string, ProbeInfo>(StringComparer.OrdinalIgnoreCase);
            panel.Accept = p =>
            {
                try { var k = FileDetector.Detect(p); return k != null && k.Category == Conversion.Category.Video; } catch { return false; }
            };
            panel.Describe = p =>
            {
                if (!Ffmpeg.Available)
                {
                    // Before the video engine is installed, use Windows' own metadata reader for the list.
                    try
                    {
                        var vp = WinRT.Await(WinRT.Open(p).Properties.GetVideoPropertiesAsync());
                        if (vp.Width > 0) return vp.Width + " × " + vp.Height + " · " + VideoCompress.FormatDuration(vp.Duration.TotalSeconds);
                    }
                    catch { }
                    return "Video";
                }
                var info = Ffmpeg.Probe(p);
                infos[p] = info;
                return info.DisplayWidth + " × " + info.DisplayHeight + " · " + VideoCompress.FormatDuration(info.Duration)
                    + (info.HasAudio ? "" : " · no audio");
            };
            panel.Thumbnail = (p, box) =>
            {
                ProbeInfo info; infos.TryGetValue(p, out info);
                double at = info != null && info.Duration > 0 ? Math.Min(1.0, info.Duration / 3) : 0;
                return Ffmpeg.Frame(p, at, box) ?? ImageUtil.IconThumb(p, box);
            };

            // ---- template <-> options ----
            bool applying = false;
            Func<CompressOptions> read = () => new CompressOptions
            {
                Codec = codec.SelectedIndex == 1 ? VCodec.H265 : VCodec.H264,
                Crf = (int)crf.Value,
                TargetMB = sizeMode.SelectedIndex == 1 ? (double)mb.Value : 0,
                Preset = Presets[Math.Max(0, speed.SelectedIndex)],
                MaxHeight = res.SelectedIndex >= 0 && res.SelectedIndex < Heights.Length ? Heights[res.SelectedIndex] : 0,
                MaxWidth = res.SelectedIndex == 7 ? 1280 : 0,
                MaxFps = FpsCaps[Math.Max(0, fps.SelectedIndex)],
                AudioKbps = AudioRates[Math.Max(0, audio.SelectedIndex)],
                AutoDownscaleForTarget = template.SelectedItem is Template && ((Template)template.SelectedItem).Options.AutoDownscaleForTarget,
            };
            Action updateEstimate = () =>
            {
                var o = read();
                var known = panel.Files.Select(f => { ProbeInfo i; return infos.TryGetValue(f, out i) ? i : null; }).Where(i => i != null).ToList();
                if (o.TargetMB > 0)
                {
                    int tooSmall = known.Count(i => VideoCompress.TargetVideoKbps(o, i) < VideoCompress.MinWatchableKbps);
                    estimate.Text = "Each video will be about " + o.TargetMB.ToString("0.#") + " MB." +
                        (tooSmall > 0 ? "  ⚠ " + tooSmall + " video" + (tooSmall == 1 ? " is" : "s are") + " too long to look good at this size." : "");
                }
                else estimate.Text = o.Codec == VCodec.H265 && o.Preset == "slow" ? "H.265 with the slow preset gives the smallest files but takes a while." : "";
            };
            Action syncRows = () =>
            {
                crfRow.Visible = sizeMode.SelectedIndex != 1; mbRow.Visible = sizeMode.SelectedIndex == 1;
                updateEstimate();
            };
            Action<Template> apply = t =>
            {
                applying = true;
                var o = t.Options;
                codec.SelectedIndex = o.Codec == VCodec.H265 ? 1 : 0;
                sizeMode.SelectedIndex = o.TargetMB > 0 ? 1 : 0;
                crf.Value = o.Crf;
                if (o.TargetMB > 0) mb.Value = (decimal)Math.Round(o.TargetMB + 0.5);
                int ri = o.MaxWidth == 1280 ? 7 : Array.IndexOf(Heights, o.MaxHeight); res.SelectedIndex = Math.Max(0, ri);
                fps.SelectedIndex = Math.Max(0, Array.IndexOf(FpsCaps, o.MaxFps));
                audio.SelectedIndex = Math.Max(0, Array.IndexOf(AudioRates, o.AudioKbps));
                speed.SelectedIndex = Math.Max(0, Array.IndexOf(Presets, o.Preset));
                hint.Text = t.Hint;
                applying = false;
                syncRows();
            };
            template.SelectedIndexChanged += delegate
            {
                var t = template.SelectedItem as Template;
                if (t != null) apply(t); else hint.Text = "Your own settings.";
            };
            EventHandler toCustom = delegate
            {
                syncRows();
                if (applying) return;
                if (template.SelectedItem is Template) { applying = true; template.SelectedIndex = template.Items.Count - 1; hint.Text = "Your own settings."; applying = false; }
            };
            foreach (var c in new[] { codec, sizeMode, res, fps, audio, speed }) c.SelectedIndexChanged += toCustom;
            crf.ValueChanged += toCustom; mb.ValueChanged += toCustom;
            panel.ItemsChanged += delegate { updateEstimate(); };
            template.SelectedIndex = 0;

            // ---- run ----
            CompressOptions snap = null;
            panel.BeforeRun = delegate
            {
                if (!Konbini.Deps.DepPrompt.Ensure(panel, Ffmpeg.Dep)) throw new OperationCanceledException();
                snap = read();
                if (sizeMode.SelectedIndex == 1 && template.SelectedItem is Template) snap.TargetMB = ((Template)template.SelectedItem).Options.TargetMB;
                else if (snap.TargetMB > 0) snap.TargetMB = snap.TargetMB * 0.96; // leave headroom so files land under the size
            };
            panel.Process = (src, baseOut) =>
            {
                var o = snap.Clone();
                string outPath = panel.ReservePath(baseOut + ".mp4");
                // Encode to a temp file next to the output, then move it into place; a failed or cancelled run leaves nothing behind.
                string tmp = Path.Combine(Path.GetDirectoryName(outPath), "." + Path.GetFileNameWithoutExtension(outPath) + ".konbini-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp4");
                string warning;
                VideoCompress.Outcome outcome;
                try
                {
                    outcome = VideoCompress.Compress(src, tmp, o, p => panel.ReportProgress(src, "Compressing " + p + "%"),
                        () => panel.CancelRequested, out warning);
                    if (outcome == VideoCompress.Outcome.Compressed)
                    {
                        if (File.Exists(outPath)) File.Delete(outPath); // only possible when the user confirmed overwriting
                        File.Move(tmp, outPath);
                    }
                }
                finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                long before = new FileInfo(src).Length;
                if (outcome == VideoCompress.Outcome.KeptOriginal) return "Already well compressed — kept original";
                long after = new FileInfo(outPath).Length;
                int pct = (int)Math.Round(100.0 * (after - before) / Math.Max(1, before));
                return "Done · " + ImageUtil.FormatBytes(before) + " → " + ImageUtil.FormatBytes(after) + " (" + (pct <= 0 ? "−" + (-pct) : "+" + pct) + "%)"
                    + (warning != null ? " · " + warning : "");
            };
            return panel;
        }
    }
}
