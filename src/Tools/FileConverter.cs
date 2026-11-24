using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Konbini.Conversion;

namespace Konbini
{
    public class FileConverterTool : ITool
    {
        public string Name { get { return "File Converter"; } }
        public string Category { get { return "Files"; } }
        public string Description { get { return "Drop any files — the type is detected from the content and you pick what to convert it to."; } }

        public Control CreateView()
        {
            var kinds = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
            var choice = new Dictionary<string, string>();           // kind id -> chosen target id (remembered)

            var opts = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // One compact "type → format" tile per detected type, wrapping three to a row.
            var rows = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(490, 0), Margin = Padding.Empty };
            // Scroll host keeps the picker to two rows so the file list keeps its space.
            var rowsHost = new Panel { AutoScroll = true, Width = 512, Height = 40, Margin = new Padding(0, 0, 0, 6) };
            rowsHost.Controls.Add(rows);
            var empty = Theme.MutedLabel("Add files to see what they can be converted to.");
            rows.Controls.Add(empty);
            opts.Controls.Add(rowsHost, 0, 0); opts.SetColumnSpan(rowsHost, 2);
            // Tiles wrap to the card's width instead of forcing the page wider.
            opts.Resize += delegate
            {
                int w = Math.Max(170, opts.ClientSize.Width - 6);
                if (rowsHost.Width == w) return;
                rowsHost.Width = w;
                rows.MaximumSize = new Size(w - SystemInformation.VerticalScrollBarWidth - 4, 0);
                rowsHost.Height = Math.Min(rows.PreferredSize.Height, 118);
            };

            var quality = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
            quality.Items.AddRange(new object[] { "High (larger files)", "Medium", "Low (smallest files)" });
            quality.SelectedIndex = 0;
            opts.Controls.Add(Ui.L("Quality:"), 0, 1); opts.Controls.Add(quality, 1, 1);
            var hint = Theme.MutedLabel("Uses Windows' built-in codecs. Multi-page PDFs give one image per page.");
            hint.MaximumSize = new Size(360, 0);
            opts.Controls.Add(hint, 0, 2); opts.SetColumnSpan(hint, 2);

            var panel = new BatchImagePanel("Convert files", opts) { Suffix = "", WarnOnEmptySuffix = false, Noun = "file" };
            panel.List.Columns[1].Text = "Type";
            panel.List.Columns[1].Width = 170;
            panel.Accept = f => true;
            panel.Describe = f =>
            {
                var k = FileDetector.Detect(f);
                lock (kinds) kinds[f] = k;
                return Converters.TargetsFor(k).Count == 0 ? k.Name + " (no conversions)" : k.Name;
            };

            // Rebuild the "convert to" rows whenever the set of detected types changes.
            string lastSignature = null;
            panel.ItemsChanged += delegate
            {
                var present = new List<FileKind>();
                lock (kinds)
                    foreach (ListViewItem it in panel.List.Items)
                    {
                        FileKind k;
                        if (kinds.TryGetValue((string)it.Tag, out k)) present.Add(k);
                    }
                var groups = present.GroupBy(k => k.Id).OrderBy(g => g.First().Category).ThenBy(g => g.Key).ToList();
                string sig = string.Join("|", groups.Select(g => g.Key + ":" + g.Count()));
                if (sig == lastSignature) return;
                lastSignature = sig;

                rows.SuspendLayout();
                rows.Controls.Clear();
                if (groups.Count == 0) rows.Controls.Add(empty);
                foreach (var g in groups)
                {
                    var kind = g.First();
                    var targets = Converters.TargetsFor(kind);
                    var tile = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                        Width = 150, Margin = new Padding(0, 0, 6, 6) };
                    var label = new Label { Text = kind.Name + " · " + g.Count(), AutoSize = true, Font = Theme.Small,
                        Margin = new Padding(1, 0, 0, 3), Tag = Theme.MutedTag };
                    tile.Controls.Add(label);
                    rows.Controls.Add(tile);
                    if (targets.Count == 0)
                    {
                        var none = new Label { Text = "Not supported", AutoSize = true, Margin = new Padding(1, 4, 0, 0) };
                        tile.Controls.Add(none);
                    }
                    else
                    {
                        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 146, Tag = kind.Id };
                        combo.Items.AddRange(targets.Cast<object>().ToArray());
                        string prev;
                        int idx = choice.TryGetValue(kind.Id, out prev) ? targets.FindIndex(t => t.Id == prev) : -1;
                        combo.SelectedIndex = idx >= 0 ? idx : 0;
                        choice[kind.Id] = targets[combo.SelectedIndex].Id;
                        combo.SelectedIndexChanged += delegate { choice[(string)combo.Tag] = ((Target)combo.SelectedItem).Id; };
                        tile.Controls.Add(combo);
                    }
                }
                rows.ResumeLayout();
                rowsHost.Height = Math.Min(rows.PreferredSize.Height, 118);
                Theme.Apply(panel); // recolour new tiles in context (inside the Options card)
            };

            // Snapshot UI choices on the UI thread before the workers start.
            Dictionary<string, string> sChoice = null; var sOpts = new ConvertOptions();
            panel.BeforeRun = delegate
            {
                sChoice = new Dictionary<string, string>(choice);
                sOpts = new ConvertOptions { Quality = quality.SelectedIndex, JpegQuality = new[] { 92, 80, 65 }[quality.SelectedIndex] };
            };
            panel.Process = (src, baseOut) =>
            {
                FileKind k;
                lock (kinds) if (!kinds.TryGetValue(src, out k)) k = FileDetector.Detect(src);
                string target;
                if (!sChoice.TryGetValue(k.Id, out target)) throw new NotSupportedException("No conversion available for " + k.Name + ".");
                return Converters.Convert(src, k, target, baseOut, sOpts, panel.ReservePath);
            };
            return panel;
        }
    }
}
