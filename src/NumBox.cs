using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Konbini
{
    // Themed numeric input: rounded frame, borderless text, arrow keys / mouse wheel to step.
    // Replaces NumericUpDown, whose spin buttons and border can't be dark-themed.
    public class NumBox : Panel
    {
        public readonly TextBox Inner;
        private decimal min, max = 100, val;
        private int decimals;
        private bool updatingText;

        public event EventHandler ValueChanged;

        public NumBox()
        {
            DoubleBuffered = true; ResizeRedraw = true;
            Height = 28; Width = 80;
            Margin = new Padding(3, 2, 3, 2);
            Inner = new TextBox { BorderStyle = BorderStyle.None, Font = Theme.Body, TextAlign = HorizontalAlignment.Left };
            Controls.Add(Inner);
            Inner.TextChanged += delegate { if (!updatingText) ParseText(); };
            Inner.Leave += delegate { WriteText(); Invalidate(); };
            Inner.Enter += delegate { Invalidate(); };
            Inner.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Up) { Step(1); e.Handled = true; }
                else if (e.KeyCode == Keys.Down) { Step(-1); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter) { WriteText(); e.SuppressKeyPress = true; }
            };
            Inner.KeyPress += (s, e) =>
            {
                // Digits, control keys and (when decimals are allowed) one decimal separator.
                string sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
                if (char.IsControl(e.KeyChar) || char.IsDigit(e.KeyChar)) return;
                if (decimals > 0 && e.KeyChar.ToString() == sep && !Inner.Text.Contains(sep)) return;
                e.Handled = true;
            };
            Inner.MouseWheel += (s, e) => Step(e.Delta > 0 ? 1 : -1);
            MouseDown += delegate { Inner.Focus(); };
            WriteText();
        }

        public decimal Minimum { get { return min; } set { min = value; if (val < min) Value = min; } }
        public decimal Maximum { get { return max; } set { max = value; if (val > max) Value = max; } }
        public int DecimalPlaces { get { return decimals; } set { decimals = Math.Max(0, value); WriteText(); } }

        public decimal Value
        {
            get { return val; }
            set
            {
                decimal v = Math.Round(Math.Max(min, Math.Min(max, value)), decimals);
                bool changed = v != val;
                val = v;
                if (!Inner.Focused || changed) WriteText();
                if (changed) { var h = ValueChanged; if (h != null) h(this, EventArgs.Empty); }
            }
        }

        // Guarded: the base Control constructor touches Text before Inner exists.
        public override string Text
        {
            get { return Inner == null ? "" : Inner.Text; }
            set { if (Inner != null) Inner.Text = value; }
        }

        private void Step(int dir)
        {
            decimal step = decimals > 0 ? (decimal)Math.Pow(10, -decimals) * 10 : 1;
            Value = val + dir * step;
            Inner.SelectionStart = Inner.Text.Length;
        }

        // Live typing updates Value (so linked fields follow immediately) without rewriting the text.
        private void ParseText()
        {
            decimal v;
            if (!decimal.TryParse(Inner.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out v)) return;
            if (v < min || v > max) return;
            v = Math.Round(v, decimals);
            if (v == val) return;
            val = v;
            var h = ValueChanged; if (h != null) h(this, EventArgs.Empty);
        }

        private void WriteText()
        {
            updatingText = true;
            Inner.Text = val.ToString("F" + decimals, CultureInfo.CurrentCulture);
            updatingText = false;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Inner == null) return; // Height/Width are set in the constructor before Inner is created
            Inner.SetBounds(8, Math.Max(0, (Height - Inner.PreferredHeight) / 2), Math.Max(10, Width - 16), Inner.PreferredHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 6))
            using (var fill = new SolidBrush(Theme.Input))
            using (var pen = new Pen(Inner.Focused ? Theme.Accent : Theme.Border))
            {
                g.FillPath(fill, p);
                g.DrawPath(pen, p);
            }
        }
    }
}
