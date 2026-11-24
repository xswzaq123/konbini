using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Konbini
{
    public static class Theme
    {
        public static readonly Color Accent = Color.FromArgb(59, 130, 246);
        public static readonly Color AccentHover = Color.FromArgb(37, 99, 235);

        // Palette for the active theme (see Refresh).
        public static Color Bg, Card, Input, Border, Hover, Text, Muted, Selected, SelectedText;
        public static Color Sidebar, SidebarText, SidebarMuted, SidebarSel, SidebarInput;
        public static bool IsDark;

        public static readonly Font Body = new Font("Segoe UI", 9.5f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 18f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9.5f);
        public static readonly Font Small = new Font("Segoe UI Semibold", 8.25f);

        // Control.Tag markers that tell Apply how to colour a control.
        public const string Primary = "primary", MutedTag = "muted", SidebarTag = "sidebar", ToggleOn = "toggle-on", ToggleOff = "toggle-off";

        static Theme() { Refresh(); }

        public static void Refresh()
        {
            IsDark = Settings.ThemeMode == Settings.ThemeDark ||
                     (Settings.ThemeMode == Settings.ThemeSystem && SystemPrefersDark());
            if (IsDark)
            {
                Bg = Color.FromArgb(17, 18, 21); Card = Color.FromArgb(26, 27, 31); Input = Color.FromArgb(35, 36, 41);
                Border = Color.FromArgb(44, 46, 53); Hover = Color.FromArgb(45, 47, 54);
                Text = Color.FromArgb(234, 235, 238); Muted = Color.FromArgb(140, 144, 154);
                Selected = Color.FromArgb(28, 45, 79); SelectedText = Color.FromArgb(147, 197, 253);
                Sidebar = Color.FromArgb(11, 12, 14); SidebarSel = Color.FromArgb(28, 30, 35); SidebarInput = Color.FromArgb(24, 25, 29);
                SidebarText = Color.FromArgb(200, 203, 210); SidebarMuted = Color.FromArgb(105, 109, 120);
            }
            else
            {
                Bg = Color.FromArgb(243, 244, 246); Card = Color.White; Input = Color.White;
                Border = Color.FromArgb(222, 225, 230); Hover = Color.FromArgb(236, 238, 242);
                Text = Color.FromArgb(22, 24, 29); Muted = Color.FromArgb(107, 112, 122);
                Selected = Color.FromArgb(219, 234, 254); SelectedText = Color.FromArgb(29, 78, 216);
                Sidebar = Color.FromArgb(24, 26, 31); SidebarSel = Color.FromArgb(44, 47, 55); SidebarInput = Color.FromArgb(38, 40, 47);
                SidebarText = Color.FromArgb(220, 222, 228); SidebarMuted = Color.FromArgb(125, 130, 141);
            }
        }

        public static bool SystemPrefersDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static Button PrimaryButton(string text)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White,
                Font = Bold, AutoSize = true, Padding = new Padding(18, 6, 18, 6), Cursor = Cursors.Hand, Tag = Primary };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = AccentHover;
            b.FlatAppearance.MouseDownBackColor = AccentHover;
            return b;
        }

        public static Button SecondaryButton(string text)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Input, ForeColor = Text, Font = Body,
                AutoSize = true, Padding = new Padding(10, 3, 10, 3), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = Border;
            return b;
        }

        public static Label MutedLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Muted, Tag = MutedTag };
        }

        // Recolours a control tree for the current theme. Safe to call repeatedly.
        public static void Apply(Control c) { Apply(c, Bg); }

        private static void Apply(Control c, Color bg)
        {
            if (Equals(c.Tag, SidebarTag)) return;
            string tag = c.Tag as string;
            Color childBg = bg;

            if (c is Card)
            {
                c.BackColor = bg;          // corners show the parent colour
                c.ForeColor = Text;
                childBg = Card;
                c.Invalidate();
            }
            else if (c is Button)
            {
                var b = (Button)c;
                if (tag == Primary)
                {
                    b.BackColor = Accent; b.ForeColor = Color.White;
                }
                else
                {
                    bool on = tag == ToggleOn;
                    b.BackColor = on ? Selected : Input;
                    b.ForeColor = on ? SelectedText : Text;
                    b.FlatAppearance.BorderColor = on ? Accent : Border;
                    b.FlatAppearance.MouseOverBackColor = on ? Selected : Hover;
                    b.FlatAppearance.MouseDownBackColor = Selected;
                }
            }
            else if (c is CheckBox && ((CheckBox)c).Appearance == Appearance.Button)
            {
                var cb = (CheckBox)c;
                cb.BackColor = Input; cb.ForeColor = Text;
                cb.FlatAppearance.BorderColor = Border;
                cb.FlatAppearance.CheckedBackColor = Selected;
                cb.FlatAppearance.MouseOverBackColor = Hover;
            }
            else if (c is CheckBox || c is RadioButton)
            {
                var bb = (ButtonBase)c;
                bb.FlatStyle = FlatStyle.Flat;   // flat glyphs follow our colours instead of the white system box
                bb.FlatAppearance.BorderColor = Muted;
                bb.FlatAppearance.CheckedBackColor = Input;
                bb.FlatAppearance.MouseOverBackColor = bg;
                c.BackColor = bg; c.ForeColor = Text;
            }
            else if (c is TextBox)
            {
                c.BackColor = Input; c.ForeColor = Text;
                ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
                NativeTheme(c, IsDark ? "DarkMode_CFD" : null);
            }
            else if (c is NumBox)
            {
                var n = (NumBox)c;
                n.BackColor = bg; n.Inner.BackColor = Input; n.Inner.ForeColor = Text;
                n.Invalidate();
                return; // its inner TextBox is styled by NumBox itself
            }
            else if (c is ComboBox)
            {
                var cbx = (ComboBox)c;
                // Light: native look (has a visible border). Dark: flat so our colours are used.
                cbx.FlatStyle = IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                c.BackColor = Input; c.ForeColor = Text;
                NativeTheme(c, IsDark ? "DarkMode_CFD" : null);
            }
            else if (c is ListView)
            {
                c.BackColor = childBg; c.ForeColor = Text;
                ListViewTheme((ListView)c);
            }
            else if (c is ListBox)
            {
                c.BackColor = Input; c.ForeColor = Text;
            }
            else if (c is Label)
            {
                c.ForeColor = tag == MutedTag ? Muted : Text;
                c.BackColor = c.Parent is ListView ? c.Parent.BackColor : bg;
            }
            else
            {
                c.ForeColor = Text;
                c.BackColor = bg;
                if (c is SlimProgress) c.Invalidate();
            }
            foreach (Control child in c.Controls) Apply(child, c is ListView ? c.BackColor : childBg);
        }

        // ---- native dark-mode hooks (no-ops on older Windows) ----

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr h, string app, string id);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        private static void WhenCreated(Control c, Action a)
        {
            if (c.IsHandleCreated) a(); else c.HandleCreated += delegate { a(); };
        }

        internal static void NativeTheme(Control c, string name)
        {
            WhenCreated(c, () => { try { SetWindowTheme(c.Handle, name, null); } catch { } });
        }

        private static void ListViewTheme(ListView lv)
        {
            WhenCreated(lv, () =>
            {
                try
                {
                    string t = IsDark ? "DarkMode_ItemsView" : "ItemsView";
                    SetWindowTheme(lv.Handle, t, null);
                    IntPtr header = SendMessage(lv.Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
                    if (header != IntPtr.Zero) SetWindowTheme(header, t, null);
                }
                catch { }
            });
        }

        // Dark title bar + Windows 11 rounded corners; silently ignored elsewhere.
        public static void TitleBar(Form f)
        {
            WhenCreated(f, () =>
            {
                try
                {
                    int v = IsDark ? 1 : 0; DwmSetWindowAttribute(f.Handle, 20, ref v, 4);
                    int round = 2; DwmSetWindowAttribute(f.Handle, 33, ref round, 4);
                    int caption = ColorTranslator.ToWin32(IsDark ? Sidebar : Color.White);
                    DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
                }
                catch { }
            });
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Rounded surface with an optional small heading; replaces GroupBox.
    public class Card : Panel
    {
        private string title;
        public int Radius = 10;

        public Card()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Padding = new Padding(14, 12, 14, 12);
        }

        public string Title
        {
            get { return title; }
            set { title = value; Padding = new Padding(14, string.IsNullOrEmpty(value) ? 12 : 38, 14, 12); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius))
            using (var fill = new SolidBrush(Theme.Card))
            using (var pen = new Pen(Theme.Border))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            if (!string.IsNullOrEmpty(title))
                TextRenderer.DrawText(g, title.ToUpperInvariant(), Theme.Small, new Point(14, 14), Theme.Muted);
        }
    }

    // Thin rounded progress bar in the accent colour.
    public class SlimProgress : Control
    {
        private int max = 100, val;
        public SlimProgress() { DoubleBuffered = true; ResizeRedraw = true; Height = 6; }
        public int Maximum { get { return max; } set { max = Math.Max(1, value); Invalidate(); } }
        public int Value { get { return val; } set { val = Math.Max(0, Math.Min(max, value)); Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = 6, y = (Height - h) / 2f;
            using (var track = Theme.RoundRect(new RectangleF(0, y, Width - 1, h), h / 2))
            using (var b = new SolidBrush(Theme.Border)) g.FillPath(b, track);
            float w = (Width - 1) * val / (float)max;
            if (w >= h)
                using (var bar = Theme.RoundRect(new RectangleF(0, y, w, h), h / 2))
                using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, bar);
        }
    }
}
