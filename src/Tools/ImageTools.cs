using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Konbini
{
    public class ImageResizerTool : ITool
    {
        public string Name { get { return "Image Resizer"; } }
        public string Category { get { return "Images"; } }
        public string Description { get { return "Resize many images at once by percentage, exact size, or to fit inside a box."; } }

        public Control CreateView()
        {
            var opts = new TableLayoutPanel { AutoSize = true, ColumnCount = 4 };
            var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
            mode.Items.AddRange(new object[] { "Fit within (max size)", "Exact size", "Percentage" });
            mode.SelectedIndex = 0;
            var w = Ui.Num(1, 50000, 1920, 80);
            var h = Ui.Num(1, 50000, 1080, 80);
            var pct = Ui.Num(1, 1000, 50, 80);
            var keep = new CheckBox { Text = "Keep aspect ratio", Checked = true, AutoSize = true };
            var noUpscale = new CheckBox { Text = "Don't enlarge smaller images", Checked = true, AutoSize = true };
            var fmt = Ui.Formats();
            var q = Ui.Num(1, 100, 90, 60);

            opts.Controls.Add(Ui.L("Mode:"), 0, 0); opts.Controls.Add(mode, 1, 0); opts.SetColumnSpan(mode, 3);
            var sizeRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
            var link = new CheckBox { Appearance = Appearance.Button, Text = "🔓", Width = 34, Height = 26,
                TextAlign = ContentAlignment.MiddleCenter, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Emoji", 10f),
                Margin = new Padding(2, 2, 6, 0), Cursor = Cursors.Hand };
            link.FlatAppearance.BorderColor = Color.FromArgb(210, 212, 218);
            link.FlatAppearance.CheckedBackColor = Theme.Selected;
            var tip = new ToolTip();
            tip.SetToolTip(link, "Lock width and height together (keeps the ratio while you type)");
            sizeRow.Controls.AddRange(new Control[] { Ui.L("W"), w, link, Ui.L("H"), h, Ui.L("px") });
            var pctRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, Visible = false };
            pctRow.Controls.AddRange(new Control[] { pct, Ui.L("%") });
            opts.Controls.Add(Ui.L("Size:"), 0, 1);
            var sizeHost = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
            sizeHost.Controls.Add(sizeRow); sizeHost.Controls.Add(pctRow);
            opts.Controls.Add(sizeHost, 1, 1); opts.SetColumnSpan(sizeHost, 3);
            opts.Controls.Add(keep, 1, 2); opts.SetColumnSpan(keep, 3);
            opts.Controls.Add(noUpscale, 1, 3); opts.SetColumnSpan(noUpscale, 3);
            opts.Controls.Add(Ui.L("Format:"), 0, 4); opts.Controls.Add(fmt, 1, 4);
            opts.Controls.Add(Ui.L("JPEG quality:"), 2, 4); opts.Controls.Add(q, 3, 4);

            mode.SelectedIndexChanged += delegate
            {
                sizeRow.Visible = mode.SelectedIndex != 2;
                pctRow.Visible = mode.SelectedIndex == 2;
                keep.Visible = mode.SelectedIndex == 1;
            };
            keep.Visible = false;

            var panel = new BatchImagePanel("Resize images", opts) { Suffix = "_resized" };
            // e.g. _resized-fit-1920x1080, _resized-800x600, _resized-50pct
            panel.AutoSuffix = () => "_resized-" + (mode.SelectedIndex == 2 ? pct.Value + "pct"
                : (mode.SelectedIndex == 0 ? "fit-" : "") + w.Value + "x" + h.Value);

            // Linked sizing: editing one dimension updates the other to keep the locked ratio (W / H).
            double ratio = 16.0 / 9.0; bool syncing = false;
            Action<NumBox, decimal> setClamped = (n, v) => n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, Math.Round(v)));
            link.CheckedChanged += delegate
            {
                link.Text = link.Checked ? "🔒" : "🔓";
                if (!link.Checked) return;
                // Lock to the selected (or first) image's ratio if there is one, otherwise to the current numbers.
                ListViewItem it = panel.List.SelectedItems.Count > 0 ? panel.List.SelectedItems[0]
                                : panel.List.Items.Count > 0 ? panel.List.Items[0] : null;
                Size s = it != null ? ImageUtil.ReadSize((string)it.Tag) : Size.Empty;
                if (!s.IsEmpty)
                {
                    ratio = (double)s.Width / s.Height;
                    syncing = true; setClamped(h, (decimal)((double)w.Value / ratio)); syncing = false;
                }
                else ratio = (double)w.Value / (double)h.Value;
                tip.SetToolTip(link, "Locked to " + (it != null && !s.IsEmpty ? Path.GetFileName((string)it.Tag) + " " : "") +
                                     "ratio " + ratio.ToString("0.###") + " : 1. Click to unlock.");
            };
            w.ValueChanged += delegate
            {
                if (!link.Checked || syncing) return;
                syncing = true; setClamped(h, (decimal)((double)w.Value / ratio)); syncing = false;
            };
            h.ValueChanged += delegate
            {
                if (!link.Checked || syncing) return;
                syncing = true; setClamped(w, (decimal)((double)h.Value * ratio)); syncing = false;
            };
            // One image, or all the same size: show that size in W × H (once per distinct size, so user edits stick).
            Size lastUniform = Size.Empty;
            panel.ItemsChanged += delegate
            {
                if (panel.SizesPending) return; // wait until every new image has been measured
                Size u = panel.UniformSize;
                if (u.IsEmpty || u == lastUniform) { if (u.IsEmpty) lastUniform = Size.Empty; return; }
                lastUniform = u;
                syncing = true;
                setClamped(w, u.Width); setClamped(h, u.Height);
                syncing = false;
                if (link.Checked) ratio = (double)u.Width / u.Height;
            };

            // Snapshot UI values on the UI thread before the worker starts.
            int sMode = 0, sW = 0, sH = 0, sQ = 0, sFmt = 0; double sPct = 1; bool sKeep = true, sNoUp = true;
            panel.BeforeRun = delegate
            {
                sMode = mode.SelectedIndex; sW = (int)w.Value; sH = (int)h.Value; sPct = (double)pct.Value / 100.0;
                sKeep = keep.Checked; sNoUp = noUpscale.Checked; sQ = (int)q.Value; sFmt = fmt.SelectedIndex;
            };
            panel.Process = (src, baseOut) =>
            {
                using (var img = ImageUtil.Load(src))
                {
                    Size target = ComputeSize(img.Size, sMode, sW, sH, sPct, sKeep, sNoUp);
                    OutFormat f = sFmt == 0 ? ImageUtil.FromExtension(src) : (OutFormat)sFmt;
                    string outPath = panel.ReservePath(baseOut + ImageUtil.ExtensionFor(f));
                    using (var res = ImageUtil.Resize(img, target.Width, target.Height))
                        ImageUtil.Save(res, outPath, f, sQ);
                    return img.Width + "×" + img.Height + " → " + target.Width + "×" + target.Height +
                           "  (" + ImageUtil.FormatBytes(new FileInfo(outPath).Length) + ")";
                }
            };
            return panel;
        }

        public static Size ComputeSize(Size s, int mode, int w, int h, double pct, bool keep, bool noUpscale)
        {
            double sx, sy;
            if (mode == 2) { sx = sy = pct; }
            else if (mode == 0) { sx = sy = Math.Min((double)w / s.Width, (double)h / s.Height); }
            else if (keep) { sx = sy = Math.Min((double)w / s.Width, (double)h / s.Height); }
            else { sx = (double)w / s.Width; sy = (double)h / s.Height; }
            if (noUpscale) { sx = Math.Min(sx, 1); sy = Math.Min(sy, 1); }
            return new Size(Math.Max(1, (int)Math.Round(s.Width * sx)), Math.Max(1, (int)Math.Round(s.Height * sy)));
        }
    }

}
