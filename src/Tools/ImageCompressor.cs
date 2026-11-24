using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Konbini
{
    public class ImageCompressorTool : ITool
    {
        public string Name { get { return "Image Compressor"; } }
        public string Category { get { return "Images"; } }
        public string Description { get { return "Shrink file size by quality level or to a target size you type in. Strips metadata."; } }

        public Control CreateView()
        {
            var opts = new TableLayoutPanel { AutoSize = true, ColumnCount = 3 };

            var byQuality = new RadioButton { Text = "By quality:", Checked = true, AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
            var quality = new TrackBar { Minimum = 1, Maximum = 100, Value = 75, TickFrequency = 10, Width = 200 };
            var qLabel = new Label { AutoSize = true, Text = "75", Font = Theme.Bold, Margin = new Padding(0, 7, 0, 0) };
            quality.ValueChanged += delegate { qLabel.Text = quality.Value.ToString(); };

            var bySize = new RadioButton { Text = "Target file size:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
            var sizeVal = new NumBox { Maximum = 1000000, Minimum = 1, Value = 200, Width = 90 };
            var unit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
            unit.Items.AddRange(new object[] { "KB", "MB" }); unit.SelectedIndex = 0;
            unit.SelectedIndexChanged += delegate { sizeVal.DecimalPlaces = unit.SelectedIndex == 1 ? 2 : 0; };
            var sizeRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
            sizeRow.Controls.AddRange(new Control[] { sizeVal, unit });

            const string qualityHint = "Lower = smaller file. 70–85 is usually visually lossless.";
            var hint = Theme.MutedLabel(qualityHint);
            var toJpeg = new CheckBox { Text = "Convert PNG/BMP/TIFF to JPEG (smaller, no transparency)", AutoSize = true };
            var maxSide = new CheckBox { Text = "Also limit longest side to", AutoSize = true };
            var maxPx = Ui.Num(100, 50000, 2560, 80);
            var keepSmaller = new CheckBox { Text = "Keep original if the result is larger", Checked = true, AutoSize = true };

            opts.Controls.Add(byQuality, 0, 0); opts.Controls.Add(quality, 1, 0); opts.Controls.Add(qLabel, 2, 0);
            opts.Controls.Add(bySize, 0, 1); opts.Controls.Add(sizeRow, 1, 1); opts.SetColumnSpan(sizeRow, 2);
            opts.Controls.Add(hint, 0, 2); opts.SetColumnSpan(hint, 3);
            opts.Controls.Add(toJpeg, 0, 3); opts.SetColumnSpan(toJpeg, 3);
            var maxRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
            maxRow.Controls.AddRange(new Control[] { maxSide, maxPx, Ui.L("px") });
            opts.Controls.Add(maxRow, 0, 4); opts.SetColumnSpan(maxRow, 3);
            opts.Controls.Add(keepSmaller, 0, 5); opts.SetColumnSpan(keepSmaller, 3);

            EventHandler modeChanged = delegate
            {
                bool s = bySize.Checked;
                qLabel.ForeColor = s ? Theme.Muted : Theme.Text;
                hint.Text = s ? "Picks the best quality that fits; shrinks dimensions if needed."
                              : qualityHint;
            };
            byQuality.CheckedChanged += modeChanged; bySize.CheckedChanged += modeChanged;
            // Touching a mode's control selects that mode (no greyed-out controls).
            quality.Scroll += delegate { byQuality.Checked = true; };
            sizeVal.ValueChanged += delegate { bySize.Checked = true; };
            unit.SelectedIndexChanged += delegate { bySize.Checked = true; };

            var panel = new BatchImagePanel("Compress images", opts) { Suffix = "_compressed" };
            // e.g. _compressed-q75, _compressed-200kb, _compressed-q75-max2560
            panel.AutoSuffix = () => "_compressed-"
                + (bySize.Checked ? sizeVal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + (unit.SelectedIndex == 1 ? "mb" : "kb") : "q" + quality.Value)
                + (maxSide.Checked ? "-max" + maxPx.Value : "");
            quality.ValueChanged += delegate { panel.RefreshSuffix(); };

            int sQ = 75, sMax = 0; long sTarget = 0; bool sJpeg = false, sKeep = true;
            panel.BeforeRun = delegate
            {
                sQ = quality.Value; sJpeg = toJpeg.Checked; sKeep = keepSmaller.Checked;
                sMax = maxSide.Checked ? (int)maxPx.Value : 0;
                sTarget = bySize.Checked ? (long)((double)sizeVal.Value * (unit.SelectedIndex == 1 ? 1024 * 1024 : 1024)) : 0;
            };
            panel.Process = (src, baseOut) => Compress(src, baseOut, sQ, sMax, sTarget, sJpeg, sKeep, panel.ReservePath);
            return panel;
        }

        public static string Compress(string src, string baseOut, int q, int maxSide, long target, bool toJpeg, bool keepSmaller,
                                      Func<string, string> reserve)
        {
            long before = new FileInfo(src).Length;
            OutFormat f = ImageUtil.FromExtension(src);
            if (toJpeg && f != OutFormat.Gif) f = OutFormat.Jpeg;
            string outPath = reserve(baseOut + ImageUtil.ExtensionFor(f));
            byte[] data; string note = "";
            using (var img = ImageUtil.Load(src))
            {
                Bitmap work = img;
                if (maxSide > 0 && Math.Max(img.Width, img.Height) > maxSide)
                {
                    Size t = ImageResizerTool.ComputeSize(img.Size, 0, maxSide, maxSide, 1, true, true);
                    work = ImageUtil.Resize(img, t.Width, t.Height);
                }
                try { data = target > 0 ? FitToSize(work, f, target, out note) : Encode(work, f, q); }
                finally { if (work != img) work.Dispose(); }
            }
            long after = data.Length;
            if (keepSmaller && after >= before && ImageUtil.FromExtension(src) == f)
            {
                if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
                    File.Copy(src, outPath, true);
                return "Already small enough, kept original (" + ImageUtil.FormatBytes(before) + ")";
            }
            File.WriteAllBytes(outPath, data);
            double saved = 100.0 * (before - after) / Math.Max(1, before);
            string r = ImageUtil.FormatBytes(before) + " → " + ImageUtil.FormatBytes(after) +
                   "  (" + (saved >= 0 ? "-" + saved.ToString("0") + "%" : "+" + (-saved).ToString("0") + "%") + ")";
            if (target > 0 && after > target) r += "  ⚠ couldn't reach target";
            return r + note;
        }

        static byte[] Encode(Bitmap b, OutFormat f, int q)
        {
            using (var ms = new MemoryStream()) { ImageUtil.Save(b, ms, f, q); return ms.ToArray(); }
        }

        // Binary-search JPEG quality for the largest file <= target; if even low quality is too big,
        // scale dimensions down and retry. Non-JPEG formats can only be scaled.
        static byte[] FitToSize(Bitmap src, OutFormat f, long target, out string note)
        {
            note = "";
            byte[] best = null;
            Bitmap cur = src;
            double scale = 1.0;
            try
            {
                for (int round = 0; round < 12; round++)
                {
                    if (f == OutFormat.Jpeg)
                    {
                        int lo = 10, hi = 95, fitQ = 0; byte[] fit = null;
                        while (lo <= hi)
                        {
                            int mid = (lo + hi) / 2;
                            byte[] d = Encode(cur, f, mid);
                            if (d.Length <= target) { fit = d; fitQ = mid; lo = mid + 1; } else hi = mid - 1;
                        }
                        if (fit != null)
                        {
                            note = "  [quality " + fitQ + (scale < 1 ? ", " + cur.Width + "×" + cur.Height : "") + "]";
                            return fit;
                        }
                        best = Encode(cur, f, 10);
                    }
                    else
                    {
                        best = Encode(cur, f, 0);
                        if (best.Length <= target) { if (scale < 1) note = "  [" + cur.Width + "×" + cur.Height + "]"; return best; }
                    }
                    // File size scales roughly with pixel count.
                    double ratio = Math.Sqrt((double)target / best.Length) * 0.95;
                    scale *= Math.Max(0.3, Math.Min(0.9, ratio));
                    int w = (int)(src.Width * scale), h = (int)(src.Height * scale);
                    if (w < 16 || h < 16) break;
                    if (cur != src) cur.Dispose();
                    cur = ImageUtil.Resize(src, w, h);
                }
                return best;
            }
            finally { if (cur != src) cur.Dispose(); }
        }
    }
}
