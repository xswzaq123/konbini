using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Konbini.Conversion;

namespace Konbini
{
    /// <summary>One suggested action: a tool and the dropped files it can take.</summary>
    public sealed class Suggestion
    {
        public ITool Tool;
        public string Reason;
        public List<string> Files = new List<string>();
        public int Priority;   // lower = suggested first when file counts tie
        /// <summary>Button text when the tool is simple enough to run right on Home with its current settings
        /// ("Compress"); null when it needs choices first, so Home opens the tool instead.</summary>
        public string Quick;
    }

    /// <summary>What the Home page found in a drop: counts per kind, suggestions, and files nothing can take.</summary>
    public sealed class DropAnalysis
    {
        public int Images, Videos, Others, Unsupported;
        public bool Truncated;
        public List<Suggestion> Suggestions = new List<Suggestion>();
        public List<string> UnsupportedFiles = new List<string>();

        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (Images > 0) parts.Add(Plural(Images, "image"));
                if (Videos > 0) parts.Add(Plural(Videos, "video"));
                if (Others > 0) parts.Add(Plural(Others, "other file"));
                if (Unsupported > 0) parts.Add(Unsupported + " not supported");
                string s = parts.Count == 0 ? "No files found" : string.Join(" · ", parts);
                return Truncated ? s + "  (first " + HomeSuggest.MaxFiles.ToString("N0") + " files only)" : s;
            }
        }

        public static string Plural(int n, string word) { return n.ToString("N0") + " " + word + (n == 1 ? "" : "s"); }
    }

    /// <summary>Decides which tools fit the dropped files, using the same rules each tool's batch uses to accept files.</summary>
    public static class HomeSuggest
    {
        public const int MaxFiles = 10000;

        // Per tool: order when several fit equally well, what it does, and the one-click button text for simple tools
        // (empty = has real choices to make, e.g. a size or a target format, so Home opens the tool).
        private static readonly Dictionary<Type, string[]> Info = new Dictionary<Type, string[]>
        {
            { typeof(ImageCompressorTool),   new[] { "0", "Make them smaller to share", "Compress" } },
            { typeof(ImageResizerTool),      new[] { "1", "Change their width and height", "" } },
            { typeof(ImageUpscalerTool),     new[] { "2", "Enlarge them with AI", "Upscale" } },
            { typeof(BackgroundRemoverTool), new[] { "3", "Cut out the subject", "Cut out" } },
            { typeof(VideoCompressorTool),   new[] { "0", "Make them smaller to share", "" } },
            { typeof(FileConverterTool),     new[] { "9", "Change their format", "" } },
        };

        /// <summary>Expands folders and classifies every file. Runs on a worker thread; checks <paramref name="cancelled"/>.</summary>
        public static DropAnalysis Analyse(IEnumerable<string> paths, IList<ITool> tools, Func<bool> cancelled)
        {
            var a = new DropAnalysis();
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                if (files.Count >= MaxFiles) { a.Truncated = true; break; }
                try
                {
                    if (Directory.Exists(p))
                    {
                        foreach (var f in Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories))
                        {
                            if (files.Count >= MaxFiles) { a.Truncated = true; break; }
                            if (seen.Add(f)) files.Add(f);
                        }
                    }
                    else if (File.Exists(p) && seen.Add(p)) files.Add(p);
                }
                catch (Exception) { } // unreadable folder: skip it, like the batch list does
                if (cancelled()) return a;
            }

            var byTool = new Dictionary<ITool, Suggestion>();
            Func<Type, Suggestion> get = t =>
            {
                var tool = tools.FirstOrDefault(x => x.GetType() == t);
                if (tool == null) return null;
                Suggestion s;
                if (!byTool.TryGetValue(tool, out s))
                {
                    string[] info;
                    Info.TryGetValue(t, out info);
                    byTool[tool] = s = new Suggestion { Tool = tool, Reason = info != null ? info[1] : tool.Description, Priority = info != null ? int.Parse(info[0]) : 5,
                        Quick = info != null && info[2] != "" ? info[2] : null };
                }
                return s;
            };

            foreach (var f in files)
            {
                if (cancelled()) return a;
                FileKind kind = null;
                try { kind = FileDetector.Detect(f); } catch (Exception) { }
                bool convertible = kind != null && Converters.TargetsFor(kind).Count > 0;
                bool any = false;

                // Image tools accept by extension (BatchImagePanel's default Accept).
                if (ImageUtil.IsImage(f))
                {
                    a.Images++; any = true;
                    foreach (var t in new[] { typeof(ImageCompressorTool), typeof(ImageResizerTool), typeof(ImageUpscalerTool), typeof(BackgroundRemoverTool) })
                    { var s = get(t); if (s != null) s.Files.Add(f); }
                }
                // Video Compressor accepts what FileDetector calls video (its own Accept rule).
                else if (kind != null && kind.Category == Category.Video)
                {
                    a.Videos++; any = true;
                    var s = get(typeof(VideoCompressorTool)); if (s != null) s.Files.Add(f);
                }
                if (convertible)
                {
                    var s = get(typeof(FileConverterTool)); if (s != null) s.Files.Add(f);
                    if (!any) { a.Others++; any = true; }
                }
                if (!any) { a.Unsupported++; if (a.UnsupportedFiles.Count < 50) a.UnsupportedFiles.Add(f); }
            }

            a.Suggestions = byTool.Values.Where(s => s.Files.Count > 0)
                .OrderByDescending(s => s.Files.Count).ThenBy(s => s.Priority).ToList();
            return a;
        }
    }

    /// <summary>The start page: drop anything, get suggestions, open a tool with the files already in its batch.</summary>
    public class HomeTool : ITool
    {
        /// <summary>Set by MainForm: the tools to suggest, how to open one with files, and how to get a tool's batch
        /// (created off-screen if needed) so simple actions can run right on Home.</summary>
        public static IList<ITool> Tools = new List<ITool>();
        public static Action<ITool, IList<string>> Open;
        public static Func<ITool, BatchImagePanel> PanelFor;

        public string Name { get { return "Home"; } }
        public string Category { get { return ""; } }
        public string Description { get { return "Drop any files or folders and Konbini suggests what to do with them."; } }

        public Control CreateView() { return new HomeView(); }
    }

    // A scrolling panel holding a full-width, auto-height layout: it can scroll down on small windows but never sideways.
    public class HomeView : Panel
    {
        private const int CardMin = 230, Gap = 12, ZoneTall = 200, ZoneShort = 64, ListHeight = 240;
        private readonly BatchImagePanel staging;
        private readonly System.Windows.Forms.Timer reanalyse;

        /// <summary>The list of dropped files shown on Home (for tests).</summary>
        public BatchImagePanel Staging { get { return staging; } }
        private readonly TableLayoutPanel body;
        private readonly DropZone zone;
        private readonly Label summary;
        private readonly FlowLayoutPanel cards;
        private readonly Label unsupported;
        private CancellationTokenSource cts;

        // The run strip: shown while (and after) a quick action runs on Home.
        private readonly Card runCard;
        private readonly Label runText;
        private readonly SlimProgress runBar;
        private readonly Button stopBtn, openFolderBtn, openToolBtn, doneBtn;
        private BatchImagePanel runPanel;
        private ITool runTool;
        private List<string> runFiles, runOutputs;

        /// <summary>The last analysis shown (for tests).</summary>
        public DropAnalysis Last { get; private set; }
        /// <summary>True while a quick action is running on Home (blocks switching pages, like a tool's batch).</summary>
        public bool Busy { get { return runPanel != null && runPanel.Busy; } }
        /// <summary>Status line of the last quick action (for tests).</summary>
        public string RunStatus { get { return runText.Text; } }

        public HomeView()
        {
            Dock = DockStyle.Fill; AutoScroll = true;
            body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, ZoneTall));   // drop zone (shrinks once files are added)
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));          // the dropped files (hidden while empty)
            for (int i = 0; i < 4; i++) body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // What was dropped, with the same toolbar as every tool: add, remove selected, clear, list/thumbnails.
            staging = new BatchImagePanel("", new Panel()) { Accept = f => true, Suffix = "", WarnOnEmptySuffix = false, Margin = new Padding(0, 0, 0, 14), Visible = false };
            staging.Noun = "file";
            staging.ShowListOnly();
            staging.ItemsChanged += delegate { StagingChanged(); };
            reanalyse = new System.Windows.Forms.Timer { Interval = 200 };
            reanalyse.Tick += delegate { reanalyse.Stop(); AnalyseStaged(); };
            Disposed += delegate { reanalyse.Dispose(); };

            zone = new DropZone { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14) };
            zone.Browse += delegate { BrowseFiles(); };
            zone.Dropped += paths => Analyse(paths);

            runCard = new Card { Title = "", Dock = DockStyle.Top, Visible = false, Margin = new Padding(0, 0, 0, 14) };
            var runLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
            runLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            runText = new Label { AutoSize = true, Font = Theme.Bold, Margin = new Padding(2, 2, 0, 8) };
            runBar = new SlimProgress { Dock = DockStyle.Top, Margin = new Padding(2, 0, 2, 10) };
            var runButtons = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = true };
            stopBtn = Theme.SecondaryButton("Stop");
            openFolderBtn = Theme.PrimaryButton("Open folder");
            openToolBtn = Theme.SecondaryButton("Open in tool");
            doneBtn = Theme.SecondaryButton("Do something else");
            foreach (var b in new[] { stopBtn, openFolderBtn, openToolBtn, doneBtn }) { b.Margin = new Padding(0, 0, 8, 0); runButtons.Controls.Add(b); }
            stopBtn.Click += delegate { if (runPanel != null) { runPanel.StopRun(); runText.Text = "Stopping after the files in progress…"; stopBtn.Enabled = false; } };
            openFolderBtn.Click += delegate { OpenFolder(); };
            openToolBtn.Click += delegate { if (HomeTool.Open != null && runTool != null) HomeTool.Open(runTool, runFiles ?? new List<string>()); };
            doneBtn.Click += delegate { runCard.Visible = false; };
            runLayout.Controls.Add(runText, 0, 0); runLayout.Controls.Add(runBar, 0, 1); runLayout.Controls.Add(runButtons, 0, 2);
            runCard.Controls.Add(runLayout);
            runCard.Resize += delegate { FitRun(); };

            summary = new Label { AutoSize = true, Font = Theme.Bold, Margin = new Padding(2, 0, 0, 8), Text = "" };
            cards = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = Padding.Empty, WrapContents = true };
            unsupported = Theme.MutedLabel(""); unsupported.Margin = new Padding(2, 6, 0, 0); unsupported.MaximumSize = new Size(600, 0);

            body.Controls.Add(zone, 0, 0);
            body.Controls.Add(staging, 0, 1);
            body.Controls.Add(runCard, 0, 2);
            body.Controls.Add(summary, 0, 3);
            body.Controls.Add(cards, 0, 4);
            body.Controls.Add(unsupported, 0, 5);
            Controls.Add(body);
            body.Resize += delegate { FitCards(); };
            BackColorChanged += delegate { Theme.NativeTheme(this, Theme.IsDark ? "DarkMode_Explorer" : null); };

            // The whole page is a drop target, not just the zone.
            AllowDrop = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
            DragEnter += (s, e) => { e.Effect = !Busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; zone.Hot = !Busy; };
            DragLeave += delegate { zone.Hot = false; };
            DragDrop += (s, e) => { zone.Hot = false; var p = e.Data.GetData(DataFormats.FileDrop) as string[]; if (p != null) Analyse(p); };
            ShowCards(null);
        }

        private void BrowseFiles()
        {
            if (Busy) return;
            using (var d = new OpenFileDialog { Multiselect = true, Title = "Choose files" })
                if (d.ShowDialog(FindForm()) == DialogResult.OK) Analyse(d.FileNames);
        }

        /// <summary>Adds dropped/browsed files or folders to the list; the suggestions follow whatever the list holds.</summary>
        public void Analyse(string[] paths)
        {
            if (Busy) return;   // a quick action is running; its files stay as they are
            runCard.Visible = false;
            staging.AddFiles(paths);
            AnalyseStaged();
        }

        // The list changed (drop, add, remove selected, clear): resize the page and refresh suggestions shortly after.
        private void StagingChanged()
        {
            bool any = staging.Files.Length > 0;
            body.RowStyles[0].Height = any ? ZoneShort : ZoneTall;
            body.RowStyles[1].Height = any ? ListHeight : 0;
            staging.Visible = any;
            zone.Compact = any;
            reanalyse.Stop(); reanalyse.Start();
        }

        /// <summary>Classifies the listed files off the UI thread, then shows the suggestions.</summary>
        private void AnalyseStaged()
        {
            reanalyse.Stop();
            if (cts != null) cts.Cancel();
            var paths = staging.Files;
            if (paths.Length == 0) { ShowCards(null); return; }
            var my = cts = new CancellationTokenSource();
            summary.Text = "Looking at your files…";
            var tools = HomeTool.Tools.ToList();
            Task.Factory.StartNew(() => HomeSuggest.Analyse(paths, tools, () => my.IsCancellationRequested), my.Token)
                .ContinueWith(t =>
                {
                    if (my.IsCancellationRequested || IsDisposed || !IsHandleCreated) return;
                    var a = t.IsFaulted ? new DropAnalysis() : t.Result;
                    try { BeginInvoke((Action)(() => { if (!my.IsCancellationRequested) ShowCards(a); })); } catch (InvalidOperationException) { }
                });
        }

        private void ShowCards(DropAnalysis a)
        {
            Last = a;
            SuspendLayout();
            foreach (Control c in cards.Controls.Cast<Control>().ToList()) c.Dispose();
            cards.Controls.Clear();
            if (a == null)
            {
                summary.Text = "Or start with a tool:";
                foreach (var t in HomeTool.Tools)
                    cards.Controls.Add(MakeCard(t, t.Description, null, null, false));
                unsupported.Text = "";
            }
            else
            {
                summary.Text = a.Summary + (a.Suggestions.Count > 0 ? " — what would you like to do?" : "");
                bool first = true;
                foreach (var s in a.Suggestions)
                {
                    cards.Controls.Add(MakeCard(s.Tool, s.Reason, s.Files, s.Quick, first));
                    first = false;
                }
                unsupported.Text = a.Unsupported == 0 ? ""
                    : "Not supported: " + DropAnalysis.Plural(a.Unsupported, "file") + " (" + string.Join(", ", a.UnsupportedFiles.Take(5).Select(Path.GetFileName)) + (a.Unsupported > 5 ? ", …" : "") + ")";
            }
            Theme.Apply(this);
            FitCards();
            ResumeLayout(true);
        }

        // Cards share the row evenly: as many columns as fit at CardMin wide, so they always fill the width without overflowing.
        private void FitCards()
        {
            int avail = body.ClientSize.Width - body.Padding.Horizontal - 6;
            if (avail <= 0 || cards.Controls.Count == 0) return;
            int n = Math.Max(1, Math.Min(cards.Controls.Count, avail / (CardMin + Gap)));
            int w = Math.Max(160, avail / n - Gap);   // each card plus its right margin fits exactly
            cards.SuspendLayout();
            foreach (Control c in cards.Controls) { c.Width = w; c.Margin = new Padding(0, 0, Gap, Gap); }
            cards.ResumeLayout(true);
        }

        private static string Noun(ITool tool) { return tool is VideoCompressorTool ? "video" : tool is FileConverterTool ? "file" : "image"; }

        private Control MakeCard(ITool tool, string reason, List<string> files, string quick, bool best)
        {
            var card = new SuggestionCard { Tool = tool, Heading = tool.Name, Reason = reason, Best = best, Files = files };
            if (files == null)
            {
                // Shortcut card (nothing dropped yet): the whole card opens the tool.
                card.Click += delegate { if (HomeTool.Open != null) HomeTool.Open(tool, new List<string>()); };
                return card;
            }
            string count = DropAnalysis.Plural(files.Count, Noun(tool));
            if (quick != null)
            {
                // Simple tool: run it right here with its current settings; the link opens the tool for more choices.
                card.Count = count;
                card.SetActions(quick, () => RunQuick(tool, files, quick), "More options", () => HomeTool.Open(tool, files));
            }
            else
            {
                // Tool with real choices (size, format, template): take the files there.
                card.Count = count;
                card.SetActions("Open tool  →", () => HomeTool.Open(tool, files), null, null);
            }
            return card;
        }

        /// <summary>Runs a simple tool on the files right here: progress, Stop, then Open folder / Open in tool.</summary>
        public void RunQuick(ITool tool, List<string> files, string verb)
        {
            if (Busy || HomeTool.PanelFor == null) return;
            var panel = HomeTool.PanelFor(tool);
            if (panel == null) { HomeTool.Open(tool, files); return; }
            panel.ClearFiles();
            panel.AddFiles(files);
            runPanel = panel; runTool = tool; runFiles = files; runOutputs = null;
            runText.Text = verb + ": " + DropAnalysis.Plural(files.Count, Noun(tool)) + "…";
            runBar.Maximum = Math.Max(1, files.Count); runBar.Value = 0; runBar.Visible = true;
            stopBtn.Visible = true; stopBtn.Enabled = true;
            openFolderBtn.Visible = openToolBtn.Visible = doneBtn.Visible = false;
            runCard.Visible = true;
            SetCardsEnabled(false);
            FitRun();

            Action<int, int> onProgress = (done, total) => { runBar.Maximum = Math.Max(1, total); runBar.Value = Math.Min(done, runBar.Maximum); runText.Text = verb + ": " + done + " of " + total + "…"; };
            Action<int, int, bool> onDone = null;
            onDone = (ok, fail, stopped) =>
            {
                panel.RunProgress -= onProgress; panel.RunCompleted -= onDone;
                runOutputs = panel.LastOutputs ?? new List<string>();
                panel.ClearFiles();   // the tool's batch is only borrowed; free it
                runText.Text = (stopped ? "Stopped. " : "Done! ") + DropAnalysis.Plural(ok, "file") + " saved next to the originals"
                    + (fail > 0 ? " · " + fail + " failed (open in tool to see why)" : "") + ".";
                runBar.Visible = false; stopBtn.Visible = false;
                openFolderBtn.Visible = runOutputs.Count > 0; openToolBtn.Visible = true; doneBtn.Visible = true;
                SetCardsEnabled(true);
                FitRun();
            };
            panel.RunProgress += onProgress;
            panel.RunCompleted += onDone;
            Theme.Apply(runCard);

            // StartRun runs the tool's own checks first (e.g. asking to download a model); it returns false if they stop it.
            if (!panel.StartRun())
            {
                panel.RunProgress -= onProgress; panel.RunCompleted -= onDone;
                panel.ClearFiles();
                runPanel = null; runCard.Visible = false;
                SetCardsEnabled(true);
            }
        }

        // Card (a Panel) doesn't shrink-wrap docked content reliably, so size it to the run strip's contents.
        private void FitRun()
        {
            var inner = runCard.Controls[0];
            int h = inner.GetPreferredSize(new Size(Math.Max(1, runCard.ClientSize.Width - runCard.Padding.Horizontal), 0)).Height + runCard.Padding.Vertical;
            if (runCard.Height != h) runCard.Height = h;
        }

        private void SetCardsEnabled(bool on) { foreach (Control c in cards.Controls) c.Enabled = on; zone.Enabled = on; }

        private void OpenFolder()
        {
            if (runOutputs == null || runOutputs.Count == 0) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + runOutputs[0] + "\""); } catch (Exception) { }
        }
    }

    /// <summary>The dashed drop area with a Browse button. Colours come from Theme at paint time.</summary>
    public class DropZone : Panel
    {
        public event Action<string[]> Dropped;
        public event EventHandler Browse;
        private bool hot;
        public bool Hot { get { return hot; } set { if (hot != value) { hot = value; Invalidate(); } } }
        private bool compact;
        /// <summary>Short one-line version once files are listed below it.</summary>
        public bool Compact { get { return compact; } set { if (compact != value) { compact = value; Place(); Invalidate(); } } }
        private readonly Button browse;

        public DropZone()
        {
            DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true);
            browse = Theme.PrimaryButton("Browse files…");
            browse.Click += (s, e) => { if (Browse != null) Browse(this, e); };
            Controls.Add(browse);
            Resize += delegate { Place(); };
            AllowDrop = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
            DragEnter += (s, e) => { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; Hot = true; };
            DragLeave += delegate { Hot = false; };
            DragDrop += (s, e) => { Hot = false; var p = e.Data.GetData(DataFormats.FileDrop) as string[]; if (p != null && Dropped != null) Dropped(p); };
        }

        private void Place()
        {
            browse.Visible = !compact;   // once files are listed, the list's own toolbar has Add files / Add folder
            browse.Location = new Point((Width - browse.Width) / 2, Height / 2 + 22);
        }

        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Place(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var path = Theme.RoundRect(r, 14))
            {
                using (var b = new SolidBrush(hot ? Color.FromArgb(40, Theme.Accent) : Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(hot ? Theme.Accent : Theme.Border, hot ? 2.5f : 1.5f) { DashStyle = DashStyle.Dash }) g.DrawPath(p, path);
            }
            if (compact)
            {
                TextRenderer.DrawText(g, hot ? "Drop to add them" : "Drop more files or folders here", Theme.Bold,
                    new Rectangle(0, 0, Width, Height), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            using (var big = new Font("Segoe UI Semibold", 15f))
                TextRenderer.DrawText(g, hot ? "Drop to see what you can do" : "Drop files or folders here", big,
                    new Rectangle(0, Height / 2 - 52, Width, 34), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "Images, videos, documents, audio, archives… Konbini suggests the right tool.", Theme.Body,
                new Rectangle(0, Height / 2 - 16, Width, 24), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>A suggestion card: tool name and what it does, plus a main button (run now, or open the tool with the
    /// files) and an optional link to the full tool. Without actions (shortcut cards) the whole card is clickable.</summary>
    public class SuggestionCard : Control
    {
        public ITool Tool;
        public string Heading, Reason, Count;
        public bool Best;
        public List<string> Files;
        private bool over;
        private Button main;
        private LinkLabel link;

        public SuggestionCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(260, 100); Margin = new Padding(0, 0, 12, 12); Cursor = Cursors.Hand; TabStop = true;
            MouseEnter += delegate { over = true; Invalidate(); };
            MouseLeave += delegate { over = false; Invalidate(); };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { if (main != null) main.PerformClick(); else OnClick(EventArgs.Empty); } };
            GotFocus += delegate { Invalidate(); }; LostFocus += delegate { Invalidate(); };
            Resize += delegate { PlaceActions(); };
        }

        /// <summary>The main button (for tests).</summary>
        public Button MainButton { get { return main; } }
        /// <summary>The "More options" link, if any (for tests).</summary>
        public LinkLabel MoreLink { get { return link; } }

        public void SetActions(string mainText, Action mainAction, string linkText, Action linkAction)
        {
            Height = 124; Cursor = Cursors.Default;
            main = Best ? Theme.PrimaryButton(mainText) : Theme.SecondaryButton(mainText);
            main.Padding = new Padding(10, 3, 10, 3);
            main.Click += delegate { mainAction(); };
            Controls.Add(main);
            if (linkText != null)
            {
                link = new LinkLabel { Text = linkText + " →", AutoSize = true, LinkBehavior = LinkBehavior.HoverUnderline, Cursor = Cursors.Hand };
                link.LinkClicked += delegate { linkAction(); };
                Controls.Add(link);
            }
            PlaceActions();
        }

        private void PlaceActions()
        {
            if (main == null) return;
            main.Location = new Point(14, Height - main.Height - 12);
            main.MaximumSize = new Size(Math.Max(80, Width - 28 - (link != null ? link.PreferredWidth + 12 : 0)), 0);
            if (link != null) link.Location = new Point(Width - link.PreferredWidth - 14, main.Top + (main.Height - link.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Color fill = over && main == null ? Theme.Hover : Theme.Card;
            if (link != null) { link.BackColor = fill; link.LinkColor = Theme.Accent; link.ActiveLinkColor = Theme.AccentHover; }
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var path = Theme.RoundRect(r, 10))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(Best || Focused ? Theme.Accent : Theme.Border, Best ? 2f : 1f)) g.DrawPath(p, path);
            }
            int x = 14;
            if (Best)
            {
                var tag = "Suggested";
                var sz = TextRenderer.MeasureText(tag, Theme.Body);
                using (var pill = Theme.RoundRect(new RectangleF(Width - sz.Width - 26, 10, sz.Width + 12, 20), 9))
                using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, pill);
                TextRenderer.DrawText(g, tag, Theme.Body, new Rectangle(Width - sz.Width - 20, 10, sz.Width + 2, 20), Color.White, TextFormatFlags.VerticalCenter);
            }
            TextRenderer.DrawText(g, Heading, Theme.Bold, new Rectangle(x, 10, Width - x - (Best ? 100 : 14), 22), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            int reasonH = main == null ? Height - 34 - 8 : (string.IsNullOrEmpty(Count) ? 36 : 18);
            TextRenderer.DrawText(g, Reason ?? "", Theme.Body, new Rectangle(x, 34, Width - x - 14, reasonH), Theme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            if (main != null && !string.IsNullOrEmpty(Count))
                TextRenderer.DrawText(g, Count, Theme.Body, new Rectangle(x, 54, Width - x - 14, 18), Theme.Text, TextFormatFlags.EndEllipsis);
        }
    }
}
