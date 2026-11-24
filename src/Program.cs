using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Konbini
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Settings.Load();
            Theme.Refresh();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private readonly List<ITool> tools = ToolRegistry.All();
        private readonly HomeTool home = new HomeTool();
        private readonly Panel content;
        private readonly ListBox nav;
        private readonly TextBox search;
        private readonly Button settingsBtn;
        private readonly Dictionary<ITool, Control> views = new Dictionary<ITool, Control>();
        private Control settingsView;
        private Panel sidebar, searchBox;

        public MainForm()
        {
            Text = "Konbini — Tool Library";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            MinimumSize = new Size(860, 560);
            ApplyStartSize();
            Font = Theme.Body;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            sidebar = new Panel { Dock = DockStyle.Left, Width = 232, Padding = new Padding(14, 16, 14, 12), Tag = Theme.SidebarTag };
            var brand = new Label { Text = "◆ Konbini", Dock = DockStyle.Top, Height = 46, ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 14f), Padding = new Padding(2, 0, 0, 0), Cursor = Cursors.Hand };
            brand.Click += delegate { GoHome(); };
            HomeTool.Tools = tools;
            HomeTool.Open = OpenToolWith;
            HomeTool.PanelFor = PanelFor;
            // Borderless search field inside a padded "pill" so it doesn't get the harsh system border.
            searchBox = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 8, 10, 6) };
            search = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = Theme.Body };
            searchBox.Controls.Add(search);
            searchBox.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var p = Theme.RoundRect(new RectangleF(0, 0, searchBox.Width - 1, searchBox.Height - 1), 8))
                using (var b = new SolidBrush(Theme.SidebarInput)) e.Graphics.FillPath(b, p);
            };
            SetCue(search, "Search tools…");
            var gap = new Panel { Dock = DockStyle.Top, Height = 14 };
            nav = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 34, IntegralHeight = false };
            nav.DrawItem += DrawNavItem;
            nav.SelectedIndexChanged += delegate
            {
                if (reverting) return;
                var t = nav.SelectedItem as ITool;
                if (t == null || t == currentTool) return;
                if (BlockedByRun()) { reverting = true; try { nav.SelectedItem = currentTool; if (nav.SelectedItem != currentTool) nav.ClearSelected(); } finally { reverting = false; } return; }
                ShowTool(t);
            };
            settingsBtn = new Button { Text = "⚙   Settings", Dock = DockStyle.Bottom, Height = 38, FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), Cursor = Cursors.Hand };
            settingsBtn.FlatAppearance.BorderSize = 0;
            settingsBtn.Click += delegate { if (content.Controls.Count == 0 || content.Controls[0] != settingsView) { if (!BlockedByRun()) ShowSettings(); } };
            sidebar.Controls.Add(nav); sidebar.Controls.Add(gap); sidebar.Controls.Add(search); sidebar.Controls.Add(brand);
            sidebar.Controls.Add(settingsBtn);

            content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18) };
            Controls.Add(content);
            Controls.Add(sidebar);

            search.TextChanged += delegate { Populate(); };
            Populate();
            GoHome();

            ApplyTheme();
            Settings.Changed += OnSettingsChanged;
        }

        private void OnSettingsChanged(object s, EventArgs e) { ApplyTheme(); }

        private void ApplyTheme()
        {
            SuspendLayout();
            BackColor = Theme.Bg;
            sidebar.BackColor = nav.BackColor = searchBox.BackColor = Theme.Sidebar;
            search.BackColor = Theme.SidebarInput; search.ForeColor = Theme.SidebarText;
            nav.ForeColor = Theme.SidebarText;
            settingsBtn.ForeColor = Theme.SidebarText;
            settingsBtn.BackColor = settingsView != null && settingsView.Parent != null ? Theme.SidebarSel : Theme.Sidebar;
            settingsBtn.FlatAppearance.MouseOverBackColor = Theme.SidebarSel;
            nav.Invalidate(); searchBox.Invalidate();
            Theme.Apply(content);
            // Cached pages that aren't on screen right now need recolouring too.
            foreach (var v in views.Values) if (v.Parent == null) Theme.Apply(v);
            if (settingsView != null && settingsView.Parent == null) Theme.Apply(settingsView);
            Theme.TitleBar(this);
            ResumeLayout();
            Invalidate(true);
        }

        // Items are category header strings followed by tools.
        private void Populate()
        {
            string q = search.Text.Trim();
            var sel = nav.SelectedItem;
            nav.BeginUpdate();
            nav.Items.Clear();
            if (q == "" || "home".IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) nav.Items.Add(home);
            foreach (var g in tools.Where(t => q == "" || (t.Name + " " + t.Category + " " + t.Description)
                         .IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).GroupBy(t => t.Category).OrderBy(g => g.Key))
            {
                nav.Items.Add(g.Key.ToUpperInvariant());
                foreach (var t in g.OrderBy(t => t.Name)) nav.Items.Add(t);
            }
            nav.EndUpdate();
            if (sel != null && nav.Items.Contains(sel)) nav.SelectedItem = sel;
        }

        private int FirstToolIndex()
        {
            for (int i = 0; i < nav.Items.Count; i++) if (nav.Items[i] is ITool) return i;
            return -1;
        }

        private void DrawNavItem(object s, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            object item = nav.Items[e.Index];
            var tool = item as ITool;
            bool selected = tool != null && (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(Theme.Sidebar)) e.Graphics.FillRectangle(bg, e.Bounds);
            if (selected)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pill = Theme.RoundRect(new RectangleF(e.Bounds.X, e.Bounds.Y + 2, e.Bounds.Width - 1, e.Bounds.Height - 4), 7))
                using (var b = new SolidBrush(Theme.SidebarSel)) e.Graphics.FillPath(b, pill);
            }
            if (tool == null)
            {
                TextRenderer.DrawText(e.Graphics, (string)item, new Font("Segoe UI Semibold", 8f),
                    new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 10, e.Bounds.Width, e.Bounds.Height - 10),
                    Theme.SidebarMuted, TextFormatFlags.Left);
            }
            else
            {
                if (selected) using (var a = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(a, e.Bounds.X, e.Bounds.Y + 6, 3, e.Bounds.Height - 12);
                TextRenderer.DrawText(e.Graphics, tool == home ? "⌂  Home" : tool.Name, tool == home ? Theme.Bold : Theme.Body,
                    new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height),
                    selected ? Color.White : Theme.SidebarText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }

        private static TableLayoutPanel Page(string title, string description, Control body)
        {
            var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // never wider than the window
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(new Label { Text = title, Font = Theme.Title, AutoSize = true }, 0, 0);
            var d = Theme.MutedLabel(description); d.Margin = new Padding(3, 2, 3, 14);
            page.Controls.Add(d, 0, 1);
            body.Dock = DockStyle.Fill;
            page.Controls.Add(body, 0, 2);
            return page;
        }

        private void ShowTool(ITool tool)
        {
            Control view;
            if (!views.TryGetValue(tool, out view))
            {
                views[tool] = view = Page(tool.Name, tool.Description, tool.CreateView());
                Theme.Apply(view);
            }
            settingsBtn.BackColor = Theme.Sidebar;
            currentTool = tool;
            ShowPage(view);
        }

        private ITool currentTool;
        private bool reverting;

        /// <summary>Shows the Home page (startup, the sidebar's Home item and the logo).</summary>
        private void GoHome()
        {
            if (currentTool == home) return;
            if (currentTool != null && BlockedByRun()) return;
            SelectInNav(home);
        }

        // Selecting in the list fires SelectedIndexChanged, which shows the page; if the item is filtered out, show it directly.
        private void SelectInNav(ITool t)
        {
            if (nav.Items.Contains(t)) nav.SelectedItem = t;
            if (currentTool != t) { reverting = true; try { nav.ClearSelected(); } finally { reverting = false; } ShowTool(t); }
        }

        /// <summary>A tool's batch panel, building its page off-screen if it hasn't been shown yet. Home borrows it to
        /// run simple actions in place, so they use exactly the tool's own processing and current settings.</summary>
        private BatchImagePanel PanelFor(ITool tool)
        {
            Control view;
            if (!views.TryGetValue(tool, out view))
            {
                views[tool] = view = Page(tool.Name, tool.Description, tool.CreateView());
                Theme.Apply(view);
            }
            var panel = Descendants(view).OfType<BatchImagePanel>().FirstOrDefault();
            if (panel != null && !panel.IsHandleCreated) { var h = panel.Handle; }   // progress is marshalled through its handle
            return panel;
        }

        /// <summary>From the Home page: open a tool with the dropped files already in its batch.</summary>
        private void OpenToolWith(ITool tool, IList<string> files)
        {
            if (currentTool != tool && currentTool != null && currentTool != home && BlockedByRun()) return;
            SelectInNav(tool);
            Control view;
            if (files.Count == 0 || !views.TryGetValue(tool, out view)) return;
            var panel = Descendants(view).OfType<BatchImagePanel>().FirstOrDefault();
            if (panel != null) panel.AddFiles(files);
        }

        // A running batch keeps its page: switching away would hide its progress and (below) clear its list.
        private bool BlockedByRun()
        {
            if (content.Controls.Count == 0) return false;
            var page = Descendants(content.Controls[0]).ToList();
            if (!page.OfType<BatchImagePanel>().Any(p => p.Busy) && !page.OfType<HomeView>().Any(h => h.Busy)) return false;
            MessageBox.Show(this, "A task is running. Wait for it to finish before switching tools.", "Konbini",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        // Starting size from Settings → Window size.
        private void ApplyStartSize()
        {
            var work = Screen.FromPoint(Cursor.Position).WorkingArea;
            StartPosition = FormStartPosition.CenterScreen;
            switch (Settings.WindowSize)
            {
                case Settings.SizeMedium: Size = new Size(Math.Min(work.Width, 1100), Math.Min(work.Height, 720)); break;
                case Settings.SizeMaximized: WindowState = FormWindowState.Maximized; goto default;
                case Settings.SizeRemember:
                    var b = Settings.LastBounds;
                    // Only reuse the old position if it's still mostly on a connected screen.
                    if (!b.IsEmpty && Screen.AllScreens.Any(s => { var r = Rectangle.Intersect(s.WorkingArea, b); return r.Width * r.Height > b.Width * b.Height / 2; }))
                    {
                        StartPosition = FormStartPosition.Manual; Bounds = b;
                        if (Settings.LastMaximized) WindowState = FormWindowState.Maximized;
                        break;
                    }
                    goto default;
                default: // Large: ~85% of the screen's usable area, at least 1100×720, never larger than the screen
                    Size = new Size(Math.Min(work.Width, Math.Max(1100, work.Width * 85 / 100)),
                                    Math.Min(work.Height, Math.Max(720, work.Height * 85 / 100)));
                    break;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Settings.Changed -= OnSettingsChanged;
            // Remember where the window was, for the "Remember last size" option.
            Settings.LastBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Settings.LastMaximized = WindowState == FormWindowState.Maximized;
            Settings.Save();
            base.OnFormClosed(e);
        }

        // Closing mid-run: ask, then let in-flight files finish (so none is left half-written) and close when done.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            var busy = views.Values.SelectMany(v => Descendants(v)).OfType<BatchImagePanel>().Where(p => p.Busy).ToList();
            if (busy.Count > 0 && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                if (MessageBox.Show(this, "Files are still being processed. Stop after the files in progress and quit?", "Konbini",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    foreach (var p in busy) p.RequestCancel();
                    // Work may have finished while the question was open; then nothing else will close the window.
                    if (!busy.Any(p => p.Busy)) BeginInvoke((Action)Close);
                }
            }
            base.OnFormClosing(e);
        }

        private static IEnumerable<Control> Descendants(Control c)
        {
            foreach (Control x in c.Controls) { yield return x; foreach (var y in Descendants(x)) yield return y; }
        }

        private void ShowSettings()
        {
            if (settingsView == null) { settingsView = Page("Settings", "Saved automatically.", new SettingsView()); Theme.Apply(settingsView); }
            currentTool = null;
            reverting = true; try { nav.ClearSelected(); } finally { reverting = false; }
            settingsBtn.BackColor = Theme.SidebarSel;
            ShowPage(settingsView);
        }

        private void ShowPage(Control view)
        {
            if (content.Controls.Count > 0 && content.Controls[0] == view) return;
            // Leaving a tool empties its batch (files, thumbnails, queued work); its options are kept.
            bool cleared = false;
            if (content.Controls.Count > 0)
                foreach (var p in Descendants(content.Controls[0]).OfType<BatchImagePanel>().ToList())
                    if (!p.Busy && p.Files.Length > 0) { p.ClearFiles(); cleared = true; }
            // Freeze painting of the content area while pages swap and lay out, then paint the finished page once.
            // Without this, every child control repaints step by step and the switch visibly flickers.
            bool frozen = content.IsHandleCreated;
            if (frozen) SendMessage(content.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                content.SuspendLayout();
                content.Controls.Clear();
                content.Controls.Add(view);
                content.ResumeLayout(true);
            }
            finally
            {
                if (frozen)
                {
                    SendMessage(content.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                    RedrawWindow(content.Handle, IntPtr.Zero, IntPtr.Zero, RDW_ERASE | RDW_FRAME | RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                }
            }
            // Free the cleared thumbnails after the new page has painted, so it doesn't delay the switch.
            if (cleared) BeginInvoke((Action)(() => GC.Collect()));
        }

        private const int WM_SETREDRAW = 0x000B;
        private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100, RDW_FRAME = 0x400;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr rgn, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        private static void SetCue(TextBox tb, string cue)
        {
            tb.HandleCreated += delegate { SendMessage(tb.Handle, 0x1501, (IntPtr)1, cue); };
        }
    }

    public class SettingsView : UserControl
    {
        public SettingsView()
        {
            Font = Theme.Body;
            var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            Controls.Add(stack);

            stack.Controls.Add(Group("Appearance", "Theme",
                new[] { Settings.ThemeSystem, Settings.ThemeLight, Settings.ThemeDark },
                new[] { "Match Windows", "Light", "Dark" },
                Settings.ThemeMode, v => Settings.ThemeMode = v));

            stack.Controls.Add(Group("Batches", "Default view for image lists",
                new[] { Settings.ViewList, Settings.ViewThumbnails },
                new[] { "☰  List", "▦  Thumbnails" },
                Settings.DefaultView, v => Settings.DefaultView = v));

            stack.Controls.Add(Group("Window", "Size when Konbini opens",
                new[] { Settings.SizeLarge, Settings.SizeMedium, Settings.SizeMaximized, Settings.SizeRemember },
                new[] { "Large", "Medium", "Maximized", "Remember last size" },
                Settings.WindowSize, v => Settings.WindowSize = v));

            stack.Controls.Add(Components());

            var path = Theme.MutedLabel("Konbini " + AppInfo.Version + (AppInfo.Portable ? " (portable — data is kept next to Konbini.exe)" : "") + "\nSettings file: " + Settings.FilePath);
            path.Margin = new Padding(3, 12, 3, 3);
            stack.Controls.Add(path);
        }

        // Dependencies: small ones are built in; large ones are downloaded on first use (or here).
        private Card Components()
        {
            var box = new Card { Title = "Components", AutoSize = true, Margin = new Padding(0, 0, 0, 14), MinimumSize = new Size(460, 0) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var refreshers = new List<Action>();
            int r = 0;
            foreach (var dep in Konbini.Deps.Registry.All)
            {
                var d = dep;
                var name = new Label { Text = d.DisplayName, AutoSize = true, Margin = new Padding(0, 8, 16, 4) };
                var state = Theme.MutedLabel(""); state.Margin = new Padding(0, 8, 16, 4);
                var buttons = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
                grid.Controls.Add(name, 0, r); grid.Controls.Add(state, 1, r); grid.Controls.Add(buttons, 2, r); r++;
                if (d.Source == Konbini.Deps.DepSource.Embedded) { state.Text = "Built in · " + ImageUtil.FormatBytes(d.Size); continue; }
                var main = Theme.SecondaryButton("Download"); main.Margin = new Padding(0, 2, 6, 2);
                var fromFile = Theme.SecondaryButton("From file…"); fromFile.Margin = new Padding(0, 2, 0, 2);
                buttons.Controls.AddRange(new Control[] { main, fromFile });
                Action refresh = () =>
                {
                    var st = Konbini.Deps.DepManager.Status(d);
                    state.Text = st == Konbini.Deps.DepStatus.Installed ? "Installed · " + ImageUtil.FormatBytes(d.Size)
                        : st == Konbini.Deps.DepStatus.Damaged ? "Damaged — needs repair" : "Not installed · " + ImageUtil.FormatBytes(d.DownloadSize) + " download";
                    main.Text = st == Konbini.Deps.DepStatus.Installed ? "Remove" : st == Konbini.Deps.DepStatus.Damaged ? "Repair" : "Download";
                    fromFile.Visible = st != Konbini.Deps.DepStatus.Installed;
                };
                refreshers.Add(refresh);
                main.Click += delegate
                {
                    if (Konbini.Deps.DepManager.IsInstalled(d))
                    {
                        if (MessageBox.Show(this, "Remove " + d.DisplayName + "? Konbini will ask to download it again when it's needed.", "Konbini",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                        try { Konbini.Deps.DepManager.Uninstall(d); }
                        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Konbini", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                    }
                    else Konbini.Deps.DepPrompt.Ensure(FindForm(), d);
                    refresh();
                };
                fromFile.Click += delegate
                {
                    using (var dlg = new OpenFileDialog { Title = "Choose " + d.DisplayName + " (" + System.IO.Path.GetFileName(new Uri(d.Url).LocalPath) + " or " + d.FileName + ")" })
                    {
                        if (dlg.ShowDialog(this) != DialogResult.OK) return;
                        try { Cursor = Cursors.WaitCursor; Konbini.Deps.DepManager.InstallFromFile(d, dlg.FileName); }
                        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Konbini", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                        finally { Cursor = Cursors.Default; }
                    }
                    refresh();
                };
            }
            var note = Theme.MutedLabel("Large components download securely from their official releases the first time a tool needs them, and are checked against a fixed fingerprint before use. Stored in " + Konbini.Deps.DepManager.Root);
            note.MaximumSize = new Size(560, 0); note.Margin = new Padding(0, 10, 0, 2);
            grid.Controls.Add(note, 0, r); grid.SetColumnSpan(note, 3);
            box.Controls.Add(grid);
            // Status is checked (hashing large files) off the UI thread whenever the page is shown or a component changes.
            Action refreshAll = () => System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var dep in Konbini.Deps.Registry.All) if (dep.Source == Konbini.Deps.DepSource.Download) Konbini.Deps.DepManager.Status(dep);
                try { BeginInvoke((Action)(() => { foreach (var f in refreshers) f(); })); } catch (InvalidOperationException) { }
            });
            Action<Konbini.Deps.Dependency> changed = _ => refreshAll();
            Konbini.Deps.DepManager.StatusChanged += changed;
            Disposed += delegate { Konbini.Deps.DepManager.StatusChanged -= changed; };
            VisibleChanged += delegate { if (Visible && IsHandleCreated) refreshAll(); };
            HandleCreated += delegate { refreshAll(); };
            return box;
        }

        private static Card Group(string title, string label, string[] values, string[] texts, string current, Action<string> set)
        {
            var box = new Card { Title = title, AutoSize = true, Margin = new Padding(0, 0, 0, 14), MinimumSize = new Size(460, 0) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            flow.Controls.Add(new Label { Text = label, AutoSize = true, Font = Theme.Bold, Margin = new Padding(0, 4, 0, 4) });
            var row = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
            for (int i = 0; i < values.Length; i++)
            {
                string v = values[i];
                var rb = new RadioButton { Text = texts[i], AutoSize = true, Checked = v == current, Margin = new Padding(0, 0, 18, 0), Name = v };
                rb.CheckedChanged += delegate { if (rb.Checked) { set(v); Settings.Save(); } };
                row.Controls.Add(rb);
            }
            flow.Controls.Add(row);
            box.Controls.Add(flow);
            return box;
        }
    }
}
