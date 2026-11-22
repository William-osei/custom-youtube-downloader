using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CustomYouTubeDownloader
{
    internal static class StudioTheme
    {
        internal static readonly Color Canvas = Color.FromArgb(245, 246, 241);
        internal static readonly Color Surface = Color.White;
        internal static readonly Color Ink = Color.FromArgb(26, 45, 37);
        internal static readonly Color Muted = Color.FromArgb(83, 101, 91);
        internal static readonly Color Accent = Color.FromArgb(19, 100, 75);
        internal static readonly Color Line = Color.FromArgb(205, 216, 207);
        internal static readonly Color Tint = Color.FromArgb(231, 242, 232);
        internal static readonly Color Rail = Color.FromArgb(18, 43, 36);
        internal static readonly Color Mint = Color.FromArgb(198, 240, 207);
        internal static readonly Color RailText = Color.FromArgb(220, 233, 222);

        internal static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float diameter = System.Math.Min(radius * 2, System.Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, diameter, diameter, 180, 90);
            path.AddArc(r.Right - diameter, r.Top, diameter, diameter, 270, 90);
            path.AddArc(r.Right - diameter, r.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(r.Left, r.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class StudioButton : Button
    {
        private readonly bool primary;
        private bool hot;
        private bool pressed;

        internal StudioButton(string label, bool primary)
        {
            this.primary = primary;
            Text = label;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Margin = new Padding(0, 0, 7, 0);
            Padding = new Padding(9, 2, 9, 2);
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(System.EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(System.EventArgs e) { hot = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 3 || Height < 3) return;
            var g = e.Graphics;
            g.Clear(Parent == null ? StudioTheme.Surface : Parent.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float unit = DeviceDpi / 96f;
            var rect = new RectangleF(0.6f, 0.6f, Width - 1.2f, Height - 1.2f);
            Color fill = !Enabled ? Color.FromArgb(238, 241, 236) :
                primary ? (pressed ? StudioTheme.Rail : hot ? Color.FromArgb(12, 81, 59) : StudioTheme.Accent) :
                (pressed || hot ? StudioTheme.Tint : StudioTheme.Surface);
            using (var shape = StudioTheme.Rounded(rect, 6 * unit))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(primary && Enabled ? fill : StudioTheme.Line, unit))
            {
                g.FillPath(brush, shape);
                g.DrawPath(pen, shape);
            }
            Color ink = !Enabled ? Color.FromArgb(108, 121, 112) : primary ? Color.White : StudioTheme.Ink;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -4, -4), ink, fill);
        }
    }
}
