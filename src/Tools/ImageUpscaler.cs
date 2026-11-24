using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Konbini.Upscale;

namespace Konbini
{
    public class ImageUpscalerTool : ITool
    {
        public string Name { get { return "Image Upscaler"; } }
        public string Category { get { return "Images"; } }
        public string Description { get { return "Enlarge images 2×, 3× or 4× with AI that rebuilds sharp detail, or a fast classic mode."; } }

        public Control CreateView()
        {
            var opts = new TableLayoutPanel { AutoSize = true, ColumnCount = 4 };
            var scale = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
            scale.Items.AddRange(new object[] { "2×", "3×", "4×" });
            scale.SelectedIndex = 0;
            var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
            mode.Items.AddRange(new object[] { "AI: best quality", "Fast (classic)" });
            mode.SelectedIndex = 0;
            var fmt = Ui.Formats();
            var q = Ui.Num(1, 100, 92, 60);
            var hint = Theme.MutedLabel("AI runs on your graphics card when available. Large images take longer.");
            hint.MaximumSize = new Size(360, 0);

            opts.Controls.Add(Ui.L("Scale:"), 0, 0); opts.Controls.Add(scale, 1, 0);
            opts.Controls.Add(Ui.L("Mode:"), 2, 0); opts.Controls.Add(mode, 3, 0);
            opts.Controls.Add(Ui.L("Format:"), 0, 1); opts.Controls.Add(fmt, 1, 1);
            opts.Controls.Add(Ui.L("JPEG quality:"), 2, 1); opts.Controls.Add(q, 3, 1);
            opts.Controls.Add(hint, 0, 2); opts.SetColumnSpan(hint, 4);

            var panel = new BatchImagePanel("Upscale images", opts) { Suffix = "_upscaled" };

            int sScale = 2, sMode = 0, sFmt = 0, sQ = 92;
            panel.BeforeRun = delegate
            {
                sScale = scale.SelectedIndex + 2; sMode = mode.SelectedIndex; sFmt = fmt.SelectedIndex; sQ = (int)q.Value;
            };
            panel.Process = (src, baseOut) =>
            {
                using (var img = ImageUtil.Load(src))
                {
                    int ow = img.Width * sScale, oh = img.Height * sScale;
                    AiUpscaler.CheckSize(ow, oh);
                    string note = "";
                    bool ai = sMode == 0;
                    if (ai && !AiUpscaler.Available) { ai = false; note = "  · AI unavailable, used Fast"; }
                    Bitmap res;
                    if (ai)
                    {
                        int last = -1;
                        res = AiUpscaler.Upscale(img, sScale,
                            p => { if (p / 5 != last / 5) { last = p; panel.ReportProgress(src, "AI " + p + "%"); } },
                            () => panel.CancelRequested);
                    }
                    else res = Lanczos.Resize(img, ow, oh, true);
                    using (res)
                    {
                        OutFormat f = sFmt == 0 ? ImageUtil.FromExtension(src) : (OutFormat)sFmt;
                        string outPath = panel.ReservePath(baseOut + ImageUtil.ExtensionFor(f));
                        ImageUtil.Save(res, outPath, f, sQ);
                        return img.Width + "×" + img.Height + " → " + ow + "×" + oh +
                               "  (" + ImageUtil.FormatBytes(new FileInfo(outPath).Length) + ")" + note;
                    }
                }
            };
            return panel;
        }
    }
}
