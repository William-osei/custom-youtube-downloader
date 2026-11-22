using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CustomYouTubeDownloader
{
    internal sealed partial class DownloaderForm
    {
        private Control CreateDownloadPanel()
        {
            var source = new DownloadCard();
            source.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            source.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var sourceHeading = Grid(3, Padding.Empty);
            sourceHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            sourceHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sourceHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var linksTitle = DownloadHeading("Video links");
            linksTitle.AutoSize = true;
            linksTitle.Margin = new Padding(0, 0, 12, 0);
            sourceHeading.Controls.Add(linksTitle, 0, 0);
            sourceHeading.Controls.Add(CreateLabel("One link per line", StudioTheme.Muted), 1, 0);
            var paste = new LinkLabel {
                Text = "Paste links", AutoSize = true, Anchor = AnchorStyles.Right,
                LinkColor = StudioTheme.Accent, ActiveLinkColor = StudioTheme.Ink,
                VisitedLinkColor = StudioTheme.Accent, Margin = Padding.Empty,
                AccessibleName = "Paste links from clipboard"
            };
            paste.LinkClicked += delegate { PasteDownloadLinks(); };
            url.EnabledChanged += delegate { paste.Enabled = url.Enabled; };
            sourceHeading.Controls.Add(paste, 2, 0);
            source.Controls.Add(sourceHeading, 0, 0);
            url.AccessibleDescription = "Enter one YouTube video or playlist URL per line.";
            source.Controls.Add(DownloadInput(url), 0, 1);

            var video = DownloadSettingsCard("Video settings");
            AddDownloadField(video, "Quality", quality, 1);
            AddDownloadField(video, "Format", format, 3);
            AddDownloadField(video, "Subtitles", captions, 5);
            video.Controls.Add(hint, 0, 7);

            var storage = DownloadSettingsCard("Folders");
            var destination = Grid(2, Padding.Empty);
            destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            destination.Controls.Add(DownloadInput(folder), 0, 0);
            browse.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            browse.Margin = new Padding(6, 0, 0, 0);
            destination.Controls.Add(browse, 1, 0);
            AddDownloadField(storage, "Download folder", destination, 1);
            AddDownloadField(storage, "Category", DownloadInput(category), 3);
            AddDownloadField(storage, "Series", DownloadInput(series), 5);
            storage.Controls.Add(CreateLabel("Leave either field blank to group automatically.", StudioTheme.Muted), 0, 7);

            var actions = Grid(2, Padding.Empty);
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            download.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            actions.Controls.Add(download, 0, 0);
            var secondaryActions = CreateActions(play, openFolder);
            secondaryActions.FlowDirection = FlowDirection.RightToLeft;
            play.Margin = Padding.Empty;
            openFolder.TabIndex = 0;
            play.TabIndex = 1;
            actions.Controls.Add(secondaryActions, 1, 0);
            return new DownloadLayout(source, video, storage, actions);
        }

        private void PasteDownloadLinks()
        {
            try
            {
                string links = Clipboard.GetText().Trim();
                if (links.Length == 0)
                {
                    SetStatus("Clipboard is empty", "Copy a video or playlist link, then choose Paste links.", false);
                    url.Focus();
                    return;
                }
                string existing = url.Text;
                if (existing.Length > 0 && !existing.EndsWith("\n") && !existing.EndsWith("\r"))
                    url.AppendText(Environment.NewLine);
                url.AppendText(links);
                url.Focus();
                SetStatus("Links added", "Review your settings, then start the download.", false);
            }
            catch (ExternalException)
            {
                SetStatus("Clipboard is busy", "Try Paste links again in a moment.", true);
            }
        }

        private static Label DownloadHeading(string text)
        {
            var heading = CreateLabel(text, StudioTheme.Ink);
            heading.Font = new Font("Segoe UI Semibold", 14, FontStyle.Regular);
            return heading;
        }

        private static DownloadCard DownloadSettingsCard(string title)
        {
            var card = new DownloadCard();
            foreach (int height in new[] { 29, 21, 33, 21, 33, 21, 33 })
                card.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.Controls.Add(DownloadHeading(title), 0, 0);
            return card;
        }

        private static void AddDownloadField(TableLayoutPanel card, string caption, Control input, int row)
        {
            card.Controls.Add(CreateLabel(caption, StudioTheme.Ink), 0, row);
            card.Controls.Add(input, 0, row + 1);
        }

        private static Control DownloadInput(TextBox editor)
        {
            var frame = new Panel {
                Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, editor.Multiline ? 0 : 3),
                Padding = new Padding(9, 5, 8, 4),
                BackColor = editor.Multiline ? StudioTheme.Tint : StudioTheme.Surface
            };
            editor.BorderStyle = BorderStyle.None;
            editor.BackColor = frame.BackColor;
            editor.ForeColor = StudioTheme.Ink;
            editor.Dock = DockStyle.Fill;
            frame.Controls.Add(editor);
            frame.Click += delegate { editor.Focus(); };
            editor.GotFocus += delegate { frame.Invalidate(); };
            editor.LostFocus += delegate { frame.Invalidate(); };
            frame.Paint += delegate(object sender, PaintEventArgs e) {
                ControlPaint.DrawBorder(e.Graphics, frame.ClientRectangle,
                    editor.Focused ? StudioTheme.Accent : StudioTheme.Line, ButtonBorderStyle.Solid);
            };
            return frame;
        }

        private sealed class DownloadCard : TableLayoutPanel
        {
            internal DownloadCard()
            {
                ColumnCount = 1;
                ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                Padding = new Padding(15, 8, 15, 12);
                Margin = Padding.Empty;
                BackColor = StudioTheme.Surface;
                DoubleBuffered = true;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(StudioTheme.Canvas);
                if (Width < 3 || Height < 3)
                    return;
                var old = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var outline = StudioTheme.Rounded(
                    new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 8f * DeviceDpi / 96f))
                using (var fill = new SolidBrush(BackColor))
                using (var line = new Pen(StudioTheme.Line))
                {
                    e.Graphics.FillPath(fill, outline);
                    e.Graphics.DrawPath(line, outline);
                }
                e.Graphics.SmoothingMode = old;
            }
        }

        private sealed class DownloadLayout : Panel
        {
            private readonly Control source, video, storage, actions;

            internal DownloadLayout(Control source, Control video, Control storage, Control actions)
            {
                SuspendLayout();
                Dock = DockStyle.Fill;
                BackColor = StudioTheme.Canvas;
                Margin = Padding.Empty;
                this.source = source;
                this.video = video;
                this.storage = storage;
                this.actions = actions;
                actions.Dock = DockStyle.None;
                Controls.AddRange(new[] { source, video, storage, actions });
                ResumeLayout();
            }

            private int Pixels(int value) { return (value * DeviceDpi + 48) / 96; }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (actions == null || Parent == null)
                    return;
                int width = ClientSize.Width;
                bool stacked = width < Pixels(566);
                int gap = Pixels(10);
                int optionsHeight = Pixels(247);
                int actionHeight = Math.Max(Pixels(38), actions.Controls[0].GetPreferredSize(Size.Empty).Height);
                int fixedHeight = (stacked ? optionsHeight * 2 + gap : optionsHeight) + gap * 2 + actionHeight;
                var minimum = new Size(Pixels(342), fixedHeight + Pixels(100));
                if (MinimumSize != minimum)
                    MinimumSize = minimum;
                var page = Parent as TabPage;
                if (page != null && page.AutoScrollMinSize != minimum)
                    page.AutoScrollMinSize = minimum;
                int composerHeight = Math.Max(Pixels(100), ClientSize.Height - fixedHeight);
                source.SetBounds(0, 0, width, composerHeight);
                int top = composerHeight + gap;
                int leftWidth = stacked ? width : (width - gap) / 2;
                video.SetBounds(0, top, leftWidth, optionsHeight);
                storage.SetBounds(stacked ? 0 : leftWidth + gap, stacked ? top + optionsHeight + gap : top,
                    stacked ? width : width - leftWidth - gap, optionsHeight);
                actions.SetBounds(0, storage.Bottom + gap, width, actionHeight);
            }
        }
    }
}
