using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace CustomYouTubeDownloader
{
    internal sealed partial class DownloaderForm
    {
        private StudioSidebar sidebar;
        private StudioMasthead masthead;
        private TableLayoutPanel shell;

        private void ConfigureStudioShell()
        {
            shell = Grid(2, Padding.Empty);
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 188));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowCount = 1;
            sidebar = new StudioSidebar(tabs, ToggleFullScreen, delegate { AppIdentity.OpenReleases(this); });
            masthead = new StudioMasthead { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
            mainLayout.Controls.Add(masthead, 0, 0);
            shell.Controls.Add(sidebar, 0, 0);
            shell.Controls.Add(mainLayout, 1, 0);
            Controls.Add(shell);
            ShowStudioPage();
            ClientSizeChanged += delegate { SizeStudioShell(); };
            Shown += delegate { ShowStudioPage(); };
            DpiChanged += delegate { BeginInvoke(new Action(SizeStudioShell)); };
        }

        private void ShowStudioPage()
        {
            int index = Math.Max(0, tabs.SelectedIndex);
            sidebar.SelectPage(index);
            masthead.SelectPage(index);
            SizeStudioShell();
        }

        private void SizeStudioShell()
        {
            if (shell == null || !IsHandleCreated || IsDisposed) return;
            float scale = DeviceDpi / 96f;
            bool compact = ClientSize.Width < 880 * scale;
            bool shortWindow = ClientSize.Height < 510 * scale;
            int rail = (int)Math.Round((compact ? 62 : 182) * scale);
            int gutter = (int)Math.Round((compact ? 10 : 20) * scale);
            int vertical = (int)Math.Round((shortWindow ? 6 : 14) * scale);
            int header = shortWindow ? 55 : tabs.SelectedIndex == 0 ? 117 : 82;
            if (shell.ColumnStyles[0].Width != rail) shell.ColumnStyles[0].Width = rail;
            var padding = new Padding(gutter, vertical, gutter, vertical);
            if (mainLayout.Padding != padding) mainLayout.Padding = padding;
            mainLayout.RowStyles[0].Height = (int)Math.Round(header * scale);
        }
    }

    // Real tab pages retain the Windows focus and selection model. The sidebar presents their labels.
    internal sealed class WorkspacePages : TabControl
    {
        internal WorkspacePages()
        {
            TabStop = false;
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(1, 1);
            Padding = Point.Empty;
        }

        public override Rectangle DisplayRectangle { get { return ClientRectangle; } }
        protected override void WndProc(ref Message message)
        {
            const int AdjustTabDisplayRectangle = 0x1328;
            if (message.Msg == AdjustTabDisplayRectangle)
            {
                message.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
        }
    }

    internal sealed class StudioMasthead : Control
    {
        private static readonly string[] Titles = { "Download studio", "Offline library", "Discover", "Watchlist", "Download history" };
        private static readonly string[] Descriptions = {
            "Save videos and playlists for offline viewing.",
            "Play and manage the videos saved on this device.",
            "Find movies and series across catalogs and streaming services.",
            "Revisit the movies and series you saved.",
            "Review previous downloads and retry interrupted jobs."
        };
        private readonly Bitmap film;
        private readonly Font headline = new Font("Georgia", 25);
        private readonly Font smallHeadline = new Font("Georgia", 17);
        private int page;

        internal StudioMasthead()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = StudioTheme.Canvas;
            AccessibleRole = AccessibleRole.StaticText;
            using (Stream stream = typeof(StudioMasthead).Assembly.GetManifestResourceStream("CustomYouTubeDownloader.Film.jpg"))
            using (var original = new Bitmap(stream)) film = new Bitmap(original);
        }

        internal void SelectPage(int index)
        {
            page = index;
            AccessibleName = Titles[index];
            AccessibleDescription = Descriptions[index];
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 4 || Height < 4) return;
            var g = e.Graphics;
            float scale = DeviceDpi / 96f;
            bool compact = Height < 58 * scale;
            bool hero = page == 0;
            int inset = hero ? (int)(22 * scale) : (int)(2 * scale);
            if (hero)
            {
                using (var clip = StudioTheme.Rounded(new RectangleF(0, 0, Width, Height), 10 * scale))
                {
                    var state = g.Save();
                    g.SetClip(clip);
                    Color baseColor = Color.FromArgb(10, 29, 22);
                    g.Clear(baseColor);
                    int w = Math.Max((int)(Height * film.Width / (float)film.Height), (int)(Width * .64));
                    int h = (int)Math.Ceiling((double)w * film.Height / film.Width);
                    int x = Width - w;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(film, x, (Height - h) / 2, w, h);
                    var fade = new Rectangle(x - 1, 0, Math.Max(2, w / 3), Height);
                    using (var brush = new LinearGradientBrush(fade, baseColor, Color.FromArgb(0,baseColor), 0f))
                        g.FillRectangle(brush, fade);
                    g.Restore(state);
                }
            }
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            var font = compact ? smallHeadline : headline;
            int titleHeight = font.Height + (int)(4 * scale);
            int subHeight = compact ? 0 : Font.Height + (int)(3 * scale);
            int y = Math.Max(0, (Height - titleHeight - subHeight) / 2);
            int textWidth = Math.Max(1, Width - inset * 2);
            TextRenderer.DrawText(g, Titles[page], font, new Rectangle(inset,y,textWidth,titleHeight),
                hero ? Color.FromArgb(248, 249, 238) : StudioTheme.Ink, flags);
            if (!compact)
                TextRenderer.DrawText(g, Descriptions[page], Font,
                    new Rectangle(inset,y+titleHeight,textWidth,subHeight),
                    hero ? Color.FromArgb(212, 229, 215) : StudioTheme.Muted, flags);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { film.Dispose(); headline.Dispose(); smallHeadline.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal sealed class StudioSidebar : Panel
    {
        private readonly StudioNavButton[] buttons = new StudioNavButton[7];
        private readonly ToolTip tips = new ToolTip();
        private readonly Bitmap logo;
        private readonly Font wordmark = new Font("Segoe UI Semibold", 11);
        private bool compact;

        internal StudioSidebar(TabControl pages, Action toggleFullScreen, Action openReleases)
        {
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            BackColor = StudioTheme.Rail;
            DoubleBuffered = true;
            AccessibleName = "Workspace navigation";
            using (var icon = new Icon(AppIdentity.Icon, 96, 96)) logo = icon.ToBitmap();
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                string label = i < pages.TabCount ? pages.TabPages[i].Text : i == 5 ? "Full screen" : "Releases";
                var button = new StudioNavButton(label, i);
                button.Name = i < 5 ? "nav" + i : i == 5 ? "fullScreen" : "releases";
                button.AccessibleName = label;
                button.TabIndex = i;
                button.Click += delegate {
                    if (index < 5) pages.SelectedIndex = index;
                    else if (index == 5) toggleFullScreen();
                    else openReleases();
                };
                tips.SetToolTip(button, i < 5 ? button.AccessibleName + " (Ctrl+" + (i + 1) + ")" :
                    i == 5 ? "Full screen (F11)" : "Open GitHub releases");
                buttons[i] = button;
                Controls.Add(button);
            }
        }

        internal void SelectPage(int index)
        {
            for (int i = 0; i < 5; i++)
            {
                buttons[i].Selected = i == index;
                buttons[i].AccessibleDescription = i == index ? "Current workspace" : "Open workspace";
                buttons[i].Invalidate();
            }
        }

        internal void SetFullScreen(bool fullScreen)
        {
            var label = fullScreen ? "Exit full screen" : "Full screen";
            buttons[5].Text = buttons[5].AccessibleName = label;
            tips.SetToolTip(buttons[5], label + " (F11)");
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (buttons == null || buttons[6] == null) return;
            float s = DeviceDpi / 96f;
            compact = Width < 120 * s;
            bool shortWindow = Height < 480 * s;
            int inset = (int)((compact ? 8 : 12) * s);
            int row = (int)((shortWindow ? 33 : 42) * s);
            int gap = (int)(4 * s);
            int top = (int)((shortWindow ? 51 : 90) * s);
            int foot = (int)((shortWindow ? 29 : 34) * s);
            for (int i = 0; i < buttons.Length; i++)
            {
                var button = buttons[i];
                button.Compact = compact;
                int y = i < 5 ? top + i * (row + gap) : Height - inset - (7 - i) * foot;
                button.SetBounds(inset, y, Math.Max(1, Width - 2 * inset), i < 5 ? row : foot);
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (logo == null || buttons[0] == null) return;
            float s = DeviceDpi / 96f;
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int size = (int)(34 * s);
            int x = compact ? (Width - size) / 2 : (int)(16 * s);
            int y = Math.Max(0, (buttons[0].Top - size) / 2);
            g.DrawImage(logo, x, y, size, size);
            if (!compact)
            {
                int left = x + size + (int)(8 * s);
                var box = new Rectangle(left, 0, Width - left - 4, buttons[0].Top);
                TextRenderer.DrawText(g, "Custom YouTube\nDownloader", wordmark, box, Color.White,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            int lineY = buttons[5].Top - (int)(6 * s);
            if (lineY > buttons[4].Bottom)
                using (var pen = new Pen(Color.FromArgb(57, 83, 68)))
                    g.DrawLine(pen, buttons[5].Left, lineY, buttons[5].Right, lineY);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tips.Dispose(); logo.Dispose(); wordmark.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal sealed class StudioNavButton : Button
    {
        private readonly int glyph;
        private bool hot;
        internal bool Selected;
        internal bool Compact;

        internal StudioNavButton(string label, int glyph)
        {
            this.glyph = glyph;
            Text = label;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = StudioTheme.Rail;
            ForeColor = StudioTheme.RailText;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(StudioTheme.Rail);
            if (Width < 4 || Height < 4) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96f;
            Color ink = Selected ? StudioTheme.Rail : ForeColor;
            Color fill = Selected ? StudioTheme.Mint : hot ? Color.FromArgb(41, 69, 55) : BackColor;
            if (fill != BackColor)
                using (var shape = StudioTheme.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 7 * s))
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, shape);
            int size = (int)(19 * s);
            int x = Compact ? (Width - size) / 2 : (int)(12 * s);
            var state = g.Save();
            g.TranslateTransform(x, (Height - size) / 2f);
            g.ScaleTransform(size / 24f, size / 24f);
            using (var p = new Pen(ink, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                DrawGlyph(g, p, glyph);
            g.Restore(state);
            if (!Compact)
            {
                int left = x + size + (int)(11 * s);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(left, 0, Width - left - 2, Height), ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -4, -4), ink, fill);
        }

        private static void DrawGlyph(Graphics g, Pen p, int glyph)
        {
            switch (glyph)
            {
                case 0:
                    g.DrawLine(p, 12, 3, 12, 15);
                    g.DrawLines(p, new[] { new Point(7, 10), new Point(12, 15), new Point(17, 10) });
                    g.DrawLines(p, new[] { new Point(3, 16), new Point(3, 20), new Point(21, 20), new Point(21, 16) });
                    break;
                case 1:
                    g.DrawRectangle(p, 3, 5, 5, 15); g.DrawRectangle(p, 9, 5, 4, 15);
                    g.DrawPolygon(p, new[] { new Point(14,6), new Point(18,5), new Point(22,19), new Point(18,20) }); break;
                case 2:
                    g.DrawEllipse(p, 3, 3, 13, 13); g.DrawLine(p, 14.5f, 14.5f, 21, 21); break;
                case 3:
                    g.DrawPolygon(p, new[] { new Point(6,3), new Point(18,3), new Point(18,21), new Point(12,17), new Point(6,21) }); break;
                case 4:
                    g.DrawArc(p, 3, 3, 18, 18, 190, 323);
                    g.DrawLines(p, new[] { new Point(2,4), new Point(2,10), new Point(8,10) });
                    g.DrawLines(p, new[] { new Point(12,7), new Point(12,12), new Point(16,14) }); break;
                case 5:
                    for (int i = 0; i < 4; i++) { g.DrawLines(p, new[] { new Point(3,9), new Point(3,3), new Point(9,3) });
                        g.TranslateTransform(12,12); g.RotateTransform(90); g.TranslateTransform(-12,-12); } break;
                default:
                    g.DrawLines(p, new[] { new Point(10,5), new Point(4,5), new Point(4,20), new Point(19,20), new Point(19,14) });
                    g.DrawLines(p, new[] { new Point(14,3), new Point(21,3), new Point(21,10) });
                    g.DrawLine(p, 21,3,11,13); break;
            }
        }
    }
}
