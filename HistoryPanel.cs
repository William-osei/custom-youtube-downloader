using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CustomYouTubeDownloader
{
    internal sealed class HistoryPanel : UserControl
    {
        private readonly DownloadHistoryStore store;
        private readonly Action<DownloadHistoryEntry> retry;
        private readonly Action<string, bool> openPath;

        private readonly ComboBox filter = DownloaderForm.CreateSelector("Filter download history");
        private readonly ListView list = new ListView();
        private readonly Label description = DownloaderForm.CreateLabel("", Color.FromArgb(176, 191, 211));
        private readonly Button play = DownloaderForm.CreateButton("Play video", true);
        private readonly Button open = DownloaderForm.CreateButton("Open folder", false);
        private readonly Button tryAgain = DownloaderForm.CreateButton("Retry video", false);
        private readonly Button copy = DownloaderForm.CreateButton("Copy YouTube link", false);
        private List<DownloadHistoryEntry> entries = new List<DownloadHistoryEntry>();
        private string storageIssue;
        private bool busy;

        internal HistoryPanel(DownloadHistoryStore store, Action<DownloadHistoryEntry> retry, Action<string, bool> openPath)
        {
            this.store = store;
            this.retry = retry;
            this.openPath = openPath;
            BackColor = Color.FromArgb(25, 37, 57);
            Dock = DockStyle.Fill;
            var card = DownloaderForm.Grid(1, new Padding(18, 12, 18, 8));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            card.RowCount = card.RowStyles.Count;
            card.Controls.Add(DownloaderForm.CreateLabel("Recent downloads", Color.FromArgb(240, 246, 254)), 0, 0);
            filter.Items.AddRange(new object[] { "All attempts", "Saved videos", "Needs retry" });
            filter.SelectedIndex = 0;
            card.Controls.Add(filter, 0, 1);

            list.Dock = DockStyle.Fill;
            list.Margin = Padding.Empty;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.BackColor = Color.FromArgb(245, 248, 253);
            list.ForeColor = Color.FromArgb(15, 23, 39);
            list.AccessibleName = "Download history";
            list.Columns.Add("Video", 330);
            list.Columns.Add("Status", 116);
            list.Columns.Add("Format", 128);
            list.Columns.Add("Started", 150);
            list.Resize += delegate { list.Columns[0].Width = Math.Max(180, list.ClientSize.Width - 414); };
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
            card.Controls.Add(list, 0, 2);
            card.Controls.Add(description, 0, 3);
            card.Controls.Add(DownloaderForm.CreateActions(play, open, tryAgain, copy), 0, 4);
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
                    if ((filter.SelectedIndex == 1 && entry.State != HistoryState.Completed) ||
                        (filter.SelectedIndex == 2 && !CanRetry(entry.State)))
                        continue;
                    string videoFormat = entry.CompatibleMp4 ? "MP4" : "MKV";
                    videoFormat += entry.MaxHeight == 0 ? " · Best" : " · ≤" + entry.MaxHeight + "p";
                    var row = new ListViewItem(new[] { entry.Title, Status(entry.State), videoFormat,
                        entry.StartedUtc.ToLocalTime().ToString("g") });
                    row.Tag = entry;
                    list.Items.Add(row);
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
            play.Enabled = file != null;
            open.Enabled = ExistingFolder(entry) != null;
            tryAgain.Enabled = !busy && entry != null && CanRetry(entry.State);
            copy.Enabled = entry != null && !String.IsNullOrWhiteSpace(entry.Url);
            if (storageIssue != null)
                description.Text = storageIssue;
            else if (entry == null)
                description.Text = list.Items.Count == 0 ? "No download attempts in this view." :
                    list.Items.Count + (list.Items.Count == 1 ? " attempt" : " attempts") + " · Select a video for details.";
            else if (entry.State == HistoryState.Completed && file == null)
                description.Text = "The saved file has moved or is unavailable.";
            else
                description.Text = String.IsNullOrWhiteSpace(entry.Note) ? Status(entry.State) : entry.Note;
        }

        private DownloadHistoryEntry Selected()
        {
            return list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as DownloadHistoryEntry : null;
        }

        private static bool CanRetry(HistoryState state)
        {
            return state == HistoryState.Failed || state == HistoryState.Cancelled || state == HistoryState.Interrupted;
        }

        private static string Status(HistoryState state)
        {
            switch (state)
            {
                case HistoryState.Running: return "In progress";
                case HistoryState.Completed: return "Saved";
                case HistoryState.Failed: return "Failed";
                case HistoryState.Cancelled: return "Cancelled";
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

        private static string VideoFile(DownloadHistoryEntry entry)
        {
            if (entry == null || entry.State != HistoryState.Completed || String.IsNullOrWhiteSpace(entry.FilePath))
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
