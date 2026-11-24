using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Konbini.BgRemove;

namespace Konbini
{
    public class BackgroundRemoverTool : ITool
    {
        public string Name { get { return "Background Remover"; } }
        public string Category { get { return "Images"; } }
        public string Description { get { return "Cut out people, products and pets with AI — keep transparency or put them on a new background."; } }

        public Control CreateView()
        {
            var opts = new TableLayoutPanel { AutoSize = true, ColumnCount = 3 };
            var model = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            var recommended = BgModels.Recommended;
            foreach (var m in BgModels.All) model.Items.Add(m);
            model.Format += (s, e) => { var m = e.ListItem as BgModel; if (m != null) e.Value = m.Name + (m == recommended ? "  (recommended)" : ""); };
            model.SelectedItem = Settings.BgModel == "" ? recommended : BgModels.ById(Settings.BgModel);
            var modelInfo = Theme.MutedLabel(""); modelInfo.MaximumSize = new Size(390, 0); modelInfo.Margin = new Padding(3, 0, 3, 8);
            var bgMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
            bgMode.Items.AddRange(new object[] { "Transparent", "Solid colour", "Blurred original", "Image…", "Mask only" });
            bgMode.SelectedIndex = 0;

            // Per-mode extras share one cell; only the relevant one is visible.
            var extra = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
            Color bgColor = Color.White;
            var swatch = new Button { Width = 30, Height = 24, FlatStyle = FlatStyle.Flat, BackColor = bgColor, Margin = new Padding(0, 2, 6, 0), Cursor = Cursors.Hand };
            var colorLbl = Theme.MutedLabel("#FFFFFF"); colorLbl.Margin = new Padding(0, 7, 0, 0);
            var colorRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, Visible = false };
            colorRow.Controls.AddRange(new Control[] { swatch, colorLbl });
            var blur = Ui.Num(1, 100, 40, 60);
            var blurRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, Visible = false };
            blurRow.Controls.AddRange(new Control[] { Ui.L("Strength:"), blur });
            string bgPath = null;
            var browse = Theme.SecondaryButton("Choose…"); browse.Margin = new Padding(0, 1, 6, 0);
            var imgLbl = Theme.MutedLabel("No image chosen"); imgLbl.Margin = new Padding(0, 7, 0, 0); imgLbl.MaximumSize = new Size(180, 0); imgLbl.AutoEllipsis = true;
            var imgRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, Visible = false };
            imgRow.Controls.AddRange(new Control[] { browse, imgLbl });
            extra.Controls.AddRange(new Control[] { colorRow, blurRow, imgRow });

            var refine = new CheckBox { Text = "Refine edges (hair, fur)", Checked = true, AutoSize = true };
            var precision = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
            precision.Items.AddRange(new object[] { "Automatic", "Fast (graphics card)", "Full precision" });
            precision.SelectedIndex = 0;
            var fmt = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
            fmt.Items.AddRange(new object[] { "PNG", "JPEG" });
            fmt.SelectedIndex = 0;
            var hint = Theme.MutedLabel("Transparent and mask results are always saved as PNG.");
            hint.MaximumSize = new Size(380, 0);

            var precLbl = Ui.L("Precision:");
            opts.Controls.Add(Ui.L("AI model:"), 0, 0); opts.Controls.Add(model, 1, 0); opts.SetColumnSpan(model, 2);
            opts.Controls.Add(modelInfo, 1, 1); opts.SetColumnSpan(modelInfo, 2);
            opts.Controls.Add(Ui.L("Background:"), 0, 2); opts.Controls.Add(bgMode, 1, 2); opts.Controls.Add(extra, 2, 2);
            opts.Controls.Add(refine, 1, 3); opts.SetColumnSpan(refine, 2);
            opts.Controls.Add(precLbl, 0, 4); opts.Controls.Add(precision, 1, 4);
            opts.Controls.Add(Ui.L("Format:"), 0, 5); opts.Controls.Add(fmt, 1, 5);
            opts.Controls.Add(hint, 0, 6); opts.SetColumnSpan(hint, 3);

            Action syncModel = () =>
            {
                var m = (BgModel)model.SelectedItem;
                string status;
                if (m == BgModels.Standard)
                {
                    var need = BackgroundRemover.NeedsFull((BgPrecision)Math.Max(0, precision.SelectedIndex)) ? Konbini.Deps.Registry.BgFull : Konbini.Deps.Registry.BgFast;
                    status = Konbini.Deps.DepManager.IsInstalled(need) ? "Downloaded · " + ImageUtil.FormatBytes(need.Size) + "."
                        : "Downloads " + ImageUtil.FormatBytes(need.Size) + " automatically when you start.";
                }
                else if (m.Dep.Source == Konbini.Deps.DepSource.Embedded) status = "Built in · " + m.SizeText + ".";
                else status = Konbini.Deps.DepManager.IsInstalled(m.Dep) ? "Downloaded · " + m.SizeText + "." : "Downloads " + m.SizeText + " automatically when you start.";
                string why = BgModels.CannotRunReason(m);
                string slow = why == null && m.GpuMinVram > 0 && !BgModels.UsesGpu(m) ? "  This PC will run it on the processor, so it will be slow." : "";
                modelInfo.Text = m.For + "\n" + m.Hardware + "\n" + status + slow + (why != null ? "\n⚠ " + why : "");
                precLbl.Visible = precision.Visible = m == BgModels.Standard;
            };
            model.SelectedIndexChanged += delegate
            {
                syncModel();
                var m = (BgModel)model.SelectedItem;
                Settings.BgModel = m == recommended && Settings.BgModel == "" ? "" : m.Id;
                Settings.Save();
            };
            Konbini.Deps.DepManager.StatusChanged += d => { try { if (model.IsHandleCreated) model.BeginInvoke((Action)syncModel); } catch (InvalidOperationException) { } };
            syncModel();
            precision.SelectedIndexChanged += delegate { syncModel(); };

            Action sync = () =>
            {
                colorRow.Visible = bgMode.SelectedIndex == 1;
                blurRow.Visible = bgMode.SelectedIndex == 2;
                imgRow.Visible = bgMode.SelectedIndex == 3;
                fmt.Enabled = bgMode.SelectedIndex == 1 || bgMode.SelectedIndex == 2 || bgMode.SelectedIndex == 3;
            };
            bgMode.SelectedIndexChanged += delegate { sync(); };
            sync();
            swatch.Click += delegate
            {
                using (var dlg = new ColorDialog { Color = bgColor, FullOpen = true })
                    if (dlg.ShowDialog(swatch.FindForm()) == DialogResult.OK)
                    {
                        bgColor = dlg.Color; swatch.BackColor = bgColor;
                        colorLbl.Text = "#" + bgColor.R.ToString("X2") + bgColor.G.ToString("X2") + bgColor.B.ToString("X2");
                    }
            };
            browse.Click += delegate
            {
                using (var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*", Title = "Choose a background image" })
                    if (dlg.ShowDialog(browse.FindForm()) == DialogResult.OK) { bgPath = dlg.FileName; imgLbl.Text = Path.GetFileName(bgPath); }
            };

            var panel = new BatchImagePanel("Remove backgrounds", opts) { Suffix = "_nobg" };

            BgModel sModel = BgModels.Standard;
            BgMode sMode = BgMode.Transparent; BgPrecision sPrec = BgPrecision.Automatic; Color sColor = Color.White;
            int sBlur = 40; bool sRefine = true, sJpeg = false; string sBgPath = null;
            panel.BeforeRun = delegate
            {
                sMode = (BgMode)bgMode.SelectedIndex; sPrec = (BgPrecision)precision.SelectedIndex; sColor = bgColor;
                sBlur = (int)blur.Value; sRefine = refine.Checked; sJpeg = fmt.SelectedIndex == 1; sBgPath = bgPath;
                sModel = (BgModel)model.SelectedItem;
                string why = BgModels.CannotRunReason(sModel);
                if (why != null)
                {
                    MessageBox.Show(panel, why, "Konbini", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    throw new OperationCanceledException();
                }
                if (sModel != BgModels.Standard)
                {
                    // Ask before downloading the model (Yes/No), then show progress with Cancel.
                    if (!Konbini.Deps.DepPrompt.Ensure(panel, sModel.Dep)) throw new OperationCanceledException();
                }
                else
                {
                    // Standard: graphics cards use the 86 MB fast copy, processors (or Full precision) the 170 MB original.
                    var need = BackgroundRemover.NeedsFull(sPrec) ? Konbini.Deps.Registry.BgFull : Konbini.Deps.Registry.BgFast;
                    if (!Konbini.Deps.DepPrompt.Ensure(panel, need)) throw new OperationCanceledException();
                }
                if (sMode == BgMode.Image && sBgPath == null)
                {
                    MessageBox.Show(panel, "Choose a background image first, or pick another background.", "Konbini",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    throw new OperationCanceledException();
                }
            };
            panel.Process = (src, baseOut) =>
            {
                using (var img = ImageUtil.Load(src))
                {
                    int last = -1;
                    float[] alpha = BackgroundRemover.Alpha(img, sModel, sPrec, sRefine, () => panel.CancelRequested,
                        p => { if (p != last) { last = p; panel.ReportProgress(src, "Cutting out " + p + "%"); } });
                    if (panel.CancelRequested) throw new OperationCanceledException();
                    Bitmap bg = null;
                    try
                    {
                        if (sMode == BgMode.Image) bg = ImageUtil.Load(sBgPath);
                        using (var res = BackgroundRemover.Compose(img, alpha, sMode, sColor, sBlur, bg))
                        {
                            bool png = sMode == BgMode.Transparent || sMode == BgMode.Mask || !sJpeg;
                            OutFormat f = png ? OutFormat.Png : OutFormat.Jpeg;
                            string outPath = panel.ReservePath(baseOut + ImageUtil.ExtensionFor(f));
                            ImageUtil.Save(res, outPath, f, 92);
                            return "Done · " + BackgroundRemover.LastDevice + "  (" + ImageUtil.FormatBytes(new FileInfo(outPath).Length) + ")";
                        }
                    }
                    finally { if (bg != null) bg.Dispose(); }
                }
            };
            return panel;
        }
    }
}
