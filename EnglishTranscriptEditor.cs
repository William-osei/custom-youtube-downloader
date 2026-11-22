using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CustomYouTubeDownloader
{
    internal sealed class EnglishTranscriptEditor : Form
    {
        private readonly string video, captions, tools;
        private EnglishTranscript document;
        private readonly ListView lines = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            HideSelection = false, MultiSelect = false, VirtualMode = true, AccessibleName = "Timed English subtitles" };
        private readonly TextBox words = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
            AccessibleName = "English subtitle text", AcceptsReturn = true, MaxLength = 0 };
        private readonly Label status = new Label { AutoSize = true, ForeColor = StudioTheme.Muted, Margin = new Padding(0, 6, 0, 2) };
        private readonly Button listen = new StudioButton("Listen to original", false);
        private readonly Button translate = new StudioButton("Retranslate from audio", false);
        private readonly Button save = new StudioButton("Save corrections", true);
        private CancellationTokenSource preview, translation;
        private SoundPlayer player;
        private MemoryStream playback;
        private int selected = -1;
        private bool loading;

        internal static void ShowFor(IWin32Window owner, string video)
        {
            try { using (var editor = new EnglishTranscriptEditor(video)) editor.ShowDialog(owner); }
            catch (Exception ex) { MessageBox.Show(owner, ex.Message, "English transcript", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private EnglishTranscriptEditor(string video)
        {
            this.video = video;
            captions = Path.ChangeExtension(video, ".en.srt");
            tools = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
            document = EnglishTranscript.Open(captions);
            Text = "Edit English text · " + Path.GetFileNameWithoutExtension(video);
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            MinimumSize = new Size(530, 400);
            ClientSize = new Size(745, 520);
            BackColor = StudioTheme.Canvas;
            ForeColor = StudioTheme.Ink;
            AppIdentity.SetWindowIcon(this);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(14) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            layout.Controls.Add(lines, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 8, 0, 6) };
            actions.Controls.Add(listen);
            actions.Controls.Add(translate);
            layout.Controls.Add(actions, 0, 1);
            layout.Controls.Add(words, 0, 2);
            layout.Controls.Add(status, 0, 3);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            var close = new StudioButton("Close", false);
            close.Click += delegate { Close(); };
            footer.Controls.Add(close);
            footer.Controls.Add(save);
            layout.Controls.Add(footer, 0, 4);

            lines.Columns.Add("Start", 106);
            lines.Columns.Add("English text", 430);
            lines.Resize += delegate { lines.Columns[1].Width = Math.Max(100, lines.ClientSize.Width - lines.Columns[0].Width - 24); };
            lines.RetrieveVirtualItem += delegate(object sender, RetrieveVirtualItemEventArgs e) {
                var line = document.Lines[e.ItemIndex];
                e.Item = new ListViewItem(new[] { line.Timestamp, line.Text.Replace('\n', ' ') });
            };
            lines.SelectedIndexChanged += delegate { SelectLine(); };
            words.TextChanged += delegate {
                if (loading || selected < 0) return;
                document.Lines[selected].Text = words.Text.Replace("\r\n", "\n").Replace('\r', '\n');
                lines.RedrawItems(selected, selected, false);
                status.Text = document.HasChanges ? "Unsaved corrections" : "No pending corrections";
                UpdateActions();
            };
            save.Click += delegate { SaveChanges(); };
            listen.Click += async delegate { await ListenAsync(); };
            translate.Click += async delegate { await RetranslateAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (document.HasChanges && MessageBox.Show(this, "Close and discard unsaved corrections?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                { e.Cancel = true; return; }
                StopListening();
                if (translation != null) translation.Cancel();
            };
            Shown += delegate { DisplayDocument(); };
        }

        private void DisplayDocument()
        {
            lines.SelectedIndices.Clear();
            lines.VirtualListSize = document.Lines.Count;
            lines.Invalidate();
            lines.Items[0].Selected = true;
            SelectLine();
            status.Text = document.Lines.Count + (document.Lines.Count == 1 ? " subtitle" : " subtitles");
        }

        private void SelectLine()
        {
            StopListening();
            selected = lines.SelectedIndices.Count == 0 ? -1 : lines.SelectedIndices[0];
            loading = true;
            try { words.Text = selected < 0 ? "" : document.Lines[selected].Text.Replace("\n", Environment.NewLine); }
            finally { loading = false; }
            UpdateActions();
        }

        private void UpdateActions()
        {
            bool ready = translation == null;
            listen.Enabled = ready && selected >= 0 && preview == null;
            words.Enabled = ready && selected >= 0;
            save.Enabled = ready && document.HasChanges;
            translate.Text = ready ? "Retranslate from audio" : "Cancel translation";
            translate.Enabled = !ready || !document.HasChanges;
        }

        private void SaveChanges()
        {
            try { if (document.Save() != null) status.Text = "Corrections saved. Create English audio to hear this version."; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            UpdateActions();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keys)
        {
            if (keys == (Keys.Control | Keys.S) && save.Enabled) { SaveChanges(); return true; }
            if (keys == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keys);
        }

        private void StopListening()
        {
            if (preview != null) { preview.Cancel(); preview = null; }
            if (player != null) { player.Stop(); player.Dispose(); player = null; }
            if (playback != null) { playback.Dispose(); playback = null; }
        }

        private async Task ListenAsync()
        {
            StopListening();
            var job = new CancellationTokenSource();
            preview = job;
            var line = document.Lines[selected];
            UpdateActions();
            try
            {
                byte[] wav = await Task.Run(() => PreviewAudio(tools, video, line.Start, line.End, job.Token), job.Token);
                if (job.IsCancellationRequested || IsDisposed) return;
                playback = new MemoryStream(wav);
                player = new SoundPlayer(playback);
                player.Load();
                player.Play();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed && !job.IsCancellationRequested) status.Text = ex.Message; }
            finally
            {
                if (preview == job) preview = null;
                job.Dispose();
                if (!IsDisposed) UpdateActions();
            }
        }

        internal static byte[] PreviewAudio(string tools, string video, double start, double end, CancellationToken token)
        {
            return EnglishTranscript.PreviewAudio(tools, video, start, end, token);
        }

        private async Task RetranslateAsync()
        {
            if (translation != null) { translation.Cancel(); return; }
            string language;
            if (!ChooseSourceLanguage(out language)) return;
            StopListening();
            var job = new CancellationTokenSource();
            translation = job;
            UpdateActions();
            status.Text = "Translating original audio…";
            IProgress<DownloadProgress> progress = new Progress<DownloadProgress>(update => {
                if (!IsDisposed && translation == job) status.Text = update.Message;
            });
            try
            {
                var result = await CaptionWorker.RetranslateEnglishAsync(tools, video, progress.Report, job.Token, language);
                if (!IsDisposed)
                {
                    var latest = EnglishTranscript.Open(captions);
                    lines.VirtualListSize = 0;
                    document = latest;
                    DisplayDocument();
                    status.Text = result.Message;
                }
            }
            catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Translation cancelled."; }
            catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; }
            finally { translation = null; job.Dispose(); if (!IsDisposed) UpdateActions(); }
        }

        private bool ChooseSourceLanguage(out string language)
        {
            language = null;
            using (var prompt = new Form { Text = "Retranslate from audio", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false,
                AutoScaleMode = AutoScaleMode.Dpi, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = StudioTheme.Canvas, ForeColor = StudioTheme.Ink, Font = Font })
            {
                AppIdentity.SetWindowIcon(prompt);
                var contents = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14) };
                prompt.Controls.Add(contents);
                contents.Controls.Add(new Label { Text = "Language spoken in the video", AutoSize = true }, 0, 0);
                var choices = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill,
                    AccessibleName = "Source language", AutoCompleteSource = AutoCompleteSource.ListItems,
                    AutoCompleteMode = AutoCompleteMode.SuggestAppend, DropDownWidth = 320 };
                var codes = CaptionWorker.SourceLanguageCodes.OrderBy(LanguageName, StringComparer.CurrentCultureIgnoreCase).ToArray();
                choices.Items.Add("Automatic (use video language or detect)");
                foreach (string code in codes) choices.Items.Add(LanguageName(code) + " (" + code + ")");
                choices.SelectedIndex = 0;
                contents.Controls.Add(choices, 0, 1);
                contents.Controls.Add(new Label { Text = "A backup of the current English transcript will be kept.",
                    AutoSize = true, Margin = new Padding(3, 8, 3, 10) }, 0, 2);
                var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                var start = new StudioButton("Translate", true) { DialogResult = DialogResult.OK };
                var cancel = new StudioButton("Cancel", false) { DialogResult = DialogResult.Cancel };
                buttons.Controls.Add(start);
                buttons.Controls.Add(cancel);
                contents.Controls.Add(buttons, 0, 3);
                prompt.AcceptButton = start;
                prompt.CancelButton = cancel;
                if (prompt.ShowDialog(this) != DialogResult.OK) return false;
                if (choices.SelectedIndex > 0) language = codes[choices.SelectedIndex - 1];
                return true;
            }
        }

        private static string LanguageName(string code)
        {
            switch (code)
            {
                case "jw": return "Javanese";
                case "haw": return "Hawaiian";
                case "nn": return "Norwegian Nynorsk";
                case "tl": return "Tagalog";
            }
            try { return CultureInfo.GetCultureInfo(code).EnglishName; }
            catch (CultureNotFoundException) { return code; }
        }
    }
}
