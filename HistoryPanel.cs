using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CustomYouTubeDownloader
{
    internal static class DownloadListLayout
    {
        internal static void FitTitleColumn(ListView list)
        {
            int lastDpi = 96;
            bool resizingForDpi = false;
            var widths = new float[list.Columns.Count];
            for (int i = 1; i < widths.Length; i++)
                widths[i] = list.Columns[i].Width;
            Action fit = delegate {
                if (!list.IsHandleCreated || list.ClientSize.Width == 0)
                    return;
                int available = list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
                for (int i = 1; i < list.Columns.Count; i++)
                    available -= list.Columns[i].Width;
                int width = Math.Max(list.Font.Height * 8, available);
                if (list.Columns[0].Width != width)
                    list.Columns[0].Width = width;
            };
            Action scale = delegate {
                resizingForDpi = true;
                lastDpi = list.DeviceDpi;
                try {
                    for (int i = 1; i < widths.Length; i++)
                        list.Columns[i].Width = (int)Math.Round(widths[i] * lastDpi / 96f);
                }
                finally { resizingForDpi = false; }
                fit();
            };
            list.HandleCreated += delegate { scale(); };
            list.DpiChangedBeforeParent += delegate { resizingForDpi = true; };
            list.DpiChangedAfterParent += delegate { scale(); };
            list.ClientSizeChanged += delegate { fit(); };
            list.ColumnWidthChanged += delegate(object sender, ColumnWidthChangedEventArgs e) {
                if (e.ColumnIndex > 0 && !resizingForDpi && list.IsHandleCreated)
                {
                    widths[e.ColumnIndex] = list.Columns[e.ColumnIndex].Width * 96f / lastDpi;
                    fit();
                }
            };
        }
    }

    internal sealed class HistoryPanel : UserControl
    {
        private readonly DownloadHistoryStore store;
        private readonly Action<DownloadHistoryEntry> retry;
        private readonly Action<string, bool> openPath;

        private readonly ComboBox filter = DownloaderForm.CreateSelector("Filter download history");
        private readonly ListView list = new ListView();
        private readonly Label description = DownloaderForm.CreateLabel("", StudioTheme.Muted);
        private readonly CollectionView collection;
        private readonly Action startDownload;
        private readonly Button play = DownloaderForm.CreateButton("Play video", true);
        private readonly Button open = DownloaderForm.CreateButton("Open folder", false);
        private readonly Button tryAgain = DownloaderForm.CreateButton("Retry video", false);
        private readonly Button copy = DownloaderForm.CreateButton("Copy YouTube link", false);
        private List<DownloadHistoryEntry> entries = new List<DownloadHistoryEntry>();
        private string storageIssue;
        private bool busy;

        internal HistoryPanel(DownloadHistoryStore store, Action<DownloadHistoryEntry> retry, Action<string, bool> openPath,
            Action startDownload = null)
        {
            this.store = store;
            this.retry = retry;
            this.openPath = openPath;
            this.startDownload = startDownload;
            BackColor = StudioTheme.Canvas;
            Dock = DockStyle.Fill;
            var card = DownloaderForm.Grid(1, Padding.Empty);
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowCount = card.RowStyles.Count;
            card.Controls.Add(DownloaderForm.CreateLabel("Filter by status", StudioTheme.Ink), 0, 0);
            filter.Items.AddRange(new object[] { "All attempts", "Saved videos", "Needs retry" });
            filter.SelectedIndex = 0;
            filter.Dock = DockStyle.None;
            filter.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            filter.Width = 190;
            card.Controls.Add(filter, 0, 1);

            list.AccessibleName = "Download history";
            list.Columns.Add("Video", 330);
            list.Columns.Add("Status", 116);
            list.Columns.Add("Format", 128);
            list.Columns.Add("Started", 150);
            collection = new CollectionView(list, CollectionKind.History);
            list.ShowItemToolTips = true;
            list.SelectedIndexChanged += delegate { UpdateSelection(); };
            list.ItemActivate += delegate { if (play.Enabled) play.PerformClick(); };
            filter.SelectedIndexChanged += delegate { Render(); };
            play.Click += delegate {
                var entry = Selected();
                string file = VideoFile(entry);
                if (file != null)
                    openPath(file, false);
                else
                    UpdateSelection();
            };
            open.Click += delegate {
                string folder = ExistingFolder(Selected());
                if (folder != null)
                    openPath(folder, true);
                else
                    UpdateSelection();
            };
            tryAgain.Click += delegate {
                var entry = Selected();
                if (entry != null && CanRetry(entry.State) && !busy)
                    retry(entry);
            };
            copy.Click += delegate {
                var entry = Selected();
                if (entry == null)
                    return;
                try
                {
                    Clipboard.SetText(entry.Url);
                    description.Text = "YouTube link copied.";
                }
                catch (ExternalException) { description.Text = "Windows couldn’t copy the link. Try again."; }
            };
            card.Controls.Add(collection, 0, 2);
            card.Controls.Add(description, 0, 3);
            card.Controls.Add(CollectionView.RowActions(play, open, tryAgain, copy), 0, 4);
            Controls.Add(card);
            UpdateSelection();
        }

        internal void Reload()
        {
            try
            {
                entries = store.Load();
                storageIssue = null;
            }
            catch (Exception ex)
            {
                if (!IsStorageError(ex))
                    throw;
                storageIssue = "Download history couldn’t be read. " + ex.Message;
            }
            Render();
        }

        internal void SetBusy(bool working)
        {
            busy = working;
            UpdateSelection();
        }

        internal void ReportStorageIssue(string message)
        {
            storageIssue = message;
            UpdateSelection();
        }

        internal static bool IsStorageError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException;
        }

        private void Render()
        {
            var old = Selected();
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var entry in entries)
                {
                    if ((filter.SelectedIndex == 1 && entry.State != HistoryState.Completed &&
                            entry.State != HistoryState.AudioPending) ||
                        (filter.SelectedIndex == 2 && !CanRetry(entry.State)))
                        continue;
                    string videoFormat = entry.CompatibleMp4 ? "MP4" : "MKV";
                    videoFormat += entry.MaxHeight == 0 ? " · Best" : " · ≤" + entry.MaxHeight + "p";
                    var row = new ListViewItem(new[] { entry.Title, Status(entry.State), videoFormat,
                        entry.StartedUtc.ToLocalTime().ToString("g") });
                    row.Tag = entry;
                    row.ToolTipText = String.IsNullOrWhiteSpace(entry.Note) ? entry.Title :
                        entry.Title + Environment.NewLine + entry.Note;
                    collection.AddRow(row);
                    if (entry.State == HistoryState.Completed && !SystemInformation.HighContrast)
                        row.SubItems[1].ForeColor = StudioTheme.Accent;
                    if (old != null && old.Id == entry.Id)
                        row.Selected = true;
                }
            }
            finally { list.EndUpdate(); }
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            var entry = Selected();
            string file = VideoFile(entry);
            bool audioPending = entry != null && entry.State == HistoryState.AudioPending;
            play.Text = audioPending ? "Play original" : "Play video";
            tryAgain.Text = audioPending && file != null ? "Retry English audio" : "Retry video";
            play.Enabled = file != null;
            open.Enabled = ExistingFolder(entry) != null;
            tryAgain.Enabled = !busy && entry != null && CanRetry(entry.State);
            copy.Enabled = entry != null && !String.IsNullOrWhiteSpace(entry.Url);
            play.Parent.Visible = list.Items.Count != 0;
            if (list.Items.Count != 0)
                collection.ShowRows();
            else if (storageIssue != null)
                collection.ShowMessage("History unavailable", "Your download history could not be read.", "Try again", Reload);
            else if (filter.SelectedIndex != 0)
                collection.ShowMessage("No matching downloads", "There are no attempts with this status.",
                    "Show all attempts", delegate { filter.SelectedIndex = 0; });
            else
                collection.ShowMessage("No downloads yet", "Downloads you start will appear here.", "Start a download", startDownload);
            if (storageIssue != null)
                description.Text = storageIssue;
            else if (entry == null)
                description.Text = list.Items.Count == 0 ? "" :
                    list.Items.Count + (list.Items.Count == 1 ? " attempt" : " attempts") + " · Select a video for details.";
            else if (audioPending && file == null)
                description.Text = "The original video has moved. Retry to download it again and add English audio.";
            else if (entry.State == HistoryState.Completed && file == null)
                description.Text = "The saved file has moved or is unavailable.";
            else
            {
                string note = String.IsNullOrWhiteSpace(entry.Note) ? Status(entry.State) : entry.Note;
                description.Text = entry.EnglishAudio ? DownloaderForm.StyleLabel(entry.AudioStyle) + " · " + note : note;
            }
        }

        private DownloadHistoryEntry Selected()
        {
            return list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as DownloadHistoryEntry : null;
        }

        private static bool CanRetry(HistoryState state)
        {
            return state == HistoryState.Failed || state == HistoryState.Cancelled || state == HistoryState.Interrupted ||
                state == HistoryState.AudioPending;
        }

        private static string Status(HistoryState state)
        {
            switch (state)
            {
                case HistoryState.Running: return "In progress";
                case HistoryState.Completed: return "Saved";
                case HistoryState.Failed: return "Failed";
                case HistoryState.Cancelled: return "Cancelled";
                case HistoryState.AudioPending: return "Audio pending";
                default: return "Interrupted";
            }
        }

        private static string ExistingFolder(DownloadHistoryEntry entry)
        {
            if (entry == null || String.IsNullOrWhiteSpace(entry.Destination))
                return null;
            try
            {
                string folder = VideoLibrary.ValidateRetryDestination(entry.Root, entry.Destination);
                return Directory.Exists(folder) ? folder : null;
            }
            catch (Exception ex)
            {
                if (!IsStorageError(ex))
                    throw;
                return null;
            }
        }

        internal static string VideoFile(DownloadHistoryEntry entry)
        {
            if (entry == null || (entry.State != HistoryState.Completed && entry.State != HistoryState.AudioPending) ||
                    String.IsNullOrWhiteSpace(entry.FilePath))
                return null;
            string folder = ExistingFolder(entry);
            if (folder == null)
                return null;
            try
            {
                string file = AppPreferences.NormalizeFolder(entry.FilePath);
                if (file == null || !String.Equals(Path.GetDirectoryName(file).TrimEnd(Path.DirectorySeparatorChar),
                        folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(Path.GetExtension(file), entry.CompatibleMp4 ? ".mp4" : ".mkv", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    return null;
                return file;
            }
            catch (Exception ex)
            {
                if (!IsStorageError(ex))
                    throw;
                return null;
            }
        }
    }
}
