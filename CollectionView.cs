using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CustomYouTubeDownloader
{
    internal enum CollectionKind { Library, Finder, Watchlist, History }

    internal sealed class CollectionView : Panel
    {
        private readonly ListView list;
        private readonly TableLayoutPanel empty;
        private readonly Control glyph;
        private readonly Label heading;
        private readonly Label note;
        private readonly LinkLabel link;
        private readonly ImageList rowHeight = new ImageList();
        private Action onAction;

        internal CollectionView(ListView list, CollectionKind kind)
        {
            this.list = list;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            BackColor = StudioTheme.Line;
            Padding = new Padding(1);
            list.Dock = DockStyle.Fill;
            list.Margin = Padding.Empty;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = StudioTheme.Surface;
            list.ForeColor = StudioTheme.Ink;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.SmallImageList = rowHeight;
            list.FontChanged += delegate { SizeRows(); };
            list.HandleCreated += delegate { SizeRows(); };
            list.OwnerDraw = !SystemInformation.HighContrast;
            list.DrawColumnHeader += DrawHeader;
            list.DrawItem += (sender, e) => e.DrawDefault = true;
            list.DrawSubItem += (sender, e) => e.DrawDefault = true;
            DownloadListLayout.FitTitleColumn(list);

            empty = DownloaderForm.Grid(1, new Padding(10, 2, 10, 2));
            empty.BackColor = StudioTheme.Surface;
            empty.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            empty.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            for (int i = 0; i < 4; i++)
                empty.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            empty.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            empty.RowCount = empty.RowStyles.Count;
            glyph = new CollectionMark(kind) { AccessibleRole = AccessibleRole.Graphic,
                AccessibleName = kind.ToString(), Size = new Size(42, 42), Margin = new Padding(0, 0, 0, 6),
                Anchor = AnchorStyles.None, TabStop = false };
            heading = new Label { AutoSize = true, Anchor = AnchorStyles.None, ForeColor = StudioTheme.Ink,
                Font = new Font("Segoe UI Semibold", 14), Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false };
            note = new Label { AutoSize = true, Anchor = AnchorStyles.None, ForeColor = StudioTheme.Muted,
                Margin = new Padding(0, 3, 0, 1), TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false };
            link = new LinkLabel { AutoSize = true, Anchor = AnchorStyles.None, LinkColor = StudioTheme.Accent,
                ActiveLinkColor = StudioTheme.Ink, VisitedLinkColor = StudioTheme.Accent,
                Margin = new Padding(0, 6, 0, 0), UseMnemonic = false };
            link.LinkClicked += delegate { if (onAction != null) onAction(); };
            empty.Controls.Add(glyph, 0, 1);
            empty.Controls.Add(heading, 0, 2);
            empty.Controls.Add(note, 0, 3);
            empty.Controls.Add(link, 0, 4);
            empty.SizeChanged += delegate {
                int width = Math.Max(40, empty.ClientSize.Width - empty.Padding.Horizontal);
                note.MaximumSize = new Size(width, 0);
                heading.MaximumSize = new Size(width, 0);
                glyph.Visible = empty.Height >= Font.Height * 8;
            };
            Controls.Add(list);
            Controls.Add(empty);
        }

        internal void ShowMessage(string title, string detail, string actionText = null, Action action = null)
        {
            heading.Text = title;
            note.Text = detail;
            link.Text = actionText ?? "";
            onAction = action;
            link.Visible = action != null;
            list.Visible = false;
            empty.Visible = true;
        }

        internal void ShowRows()
        {
            empty.Visible = false;
            list.Visible = true;
        }

        internal static FlowLayoutPanel RowActions(params Button[] buttons)
        {
            var actions = DownloaderForm.CreateActions(buttons);
            actions.AutoSize = true;
            actions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actions.WrapContents = true;
            return actions;
        }

        internal void AddRow(ListViewItem row)
        {
            row.UseItemStyleForSubItems = false;
            if (!SystemInformation.HighContrast)
            {
                Color background = list.Items.Count % 2 == 0 ? StudioTheme.Surface : StudioTheme.Canvas;
                for (int i = 0; i < row.SubItems.Count; i++)
                {
                    row.SubItems[i].BackColor = background;
                    row.SubItems[i].ForeColor = i == 0 ? StudioTheme.Ink : StudioTheme.Muted;
                }
            }
            list.Items.Add(row);
        }

        private void SizeRows()
        {
            var size = new Size(1, Math.Min(256, list.Font.Height + 14 * list.DeviceDpi / 96));
            if (rowHeight.ImageSize != size)
                rowHeight.ImageSize = size;
        }

        private void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var background = new SolidBrush(StudioTheme.Tint))
                e.Graphics.FillRectangle(background, e.Bounds);
            using (var line = new Pen(StudioTheme.Line))
                e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var text = Rectangle.Inflate(e.Bounds, -4 * list.DeviceDpi / 96, 0);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, list.Font, text, StudioTheme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                list.SmallImageList = null;
                rowHeight.Dispose();
                heading.Font.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class CollectionMark : Control
        {
            private readonly CollectionKind kind;
            internal CollectionMark(CollectionKind kind) { this.kind = kind; DoubleBuffered = true; }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(ClientSize.Width / 42f, ClientSize.Height / 42f);
                using (var tint = new SolidBrush(StudioTheme.Tint))
                    g.FillEllipse(tint, 0, 0, 41, 41);
                using (var pen = new Pen(StudioTheme.Accent, 1.7f))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    switch (kind)
                    {
                        case CollectionKind.Finder:
                            g.DrawEllipse(pen, 11, 10, 14, 14);
                            g.DrawLine(pen, 23, 23, 30, 30);
                            break;
                        case CollectionKind.Watchlist:
                            g.DrawPolygon(pen, new[] { new Point(14, 11), new Point(28, 11),
                                new Point(28, 30), new Point(21, 25), new Point(14, 30) });
                            break;
                        case CollectionKind.History:
                            g.DrawEllipse(pen, 11, 11, 20, 20);
                            g.DrawLines(pen, new[] { new Point(21, 15), new Point(21, 21), new Point(26, 24) });
                            break;
                        default:
                            g.DrawRectangle(pen, 11, 14, 20, 14);
                            g.DrawLine(pen, 14, 11, 28, 11);
                            g.DrawPolygon(pen, new[] { new Point(19, 18), new Point(24, 21), new Point(19, 24) });
                            break;
                    }
                }
            }
        }
    }
}
