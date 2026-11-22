using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CustomYouTubeDownloader
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string user;
            using (var identity = WindowsIdentity.GetCurrent())
                user = identity.User.Value;
            bool first;
            using (var instance = new Mutex(true, @"Local\CustomYouTubeDownloader-" + user, out first))
            {
                if (!first)
                {
                    MessageBox.Show("The downloader is already open.", "Custom YouTube Downloader",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { Application.Run(new DownloaderForm()); }
                finally { instance.ReleaseMutex(); }
            }
        }
    }

    internal sealed partial class DownloaderForm : Form
    {
        private static readonly Color Background = StudioTheme.Canvas;
        private static readonly Color TextColor = StudioTheme.Ink;
        private static readonly Color Secondary = StudioTheme.Muted;
        private static readonly Color Error = Color.FromArgb(155, 49, 43);
        private static readonly int[] QualityLimits = { 0, 2160, 1440, 1080, 720 };
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Custom YouTube Downloader");
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string first, string second);
        private readonly AppPreferences preferences;
        private readonly DiscoveryPanel finder;
        private readonly WatchlistPanel watchlist;
        private readonly DownloadHistoryStore historyStore;
        private readonly HistoryPanel history;
        private readonly TextBox url = CreateInput("YouTube video link");
        private readonly TextBox folder = CreateInput("Download folder");
        private readonly TextBox category = CreateInput("Category");
        private readonly TextBox series = CreateInput("Series");
        private readonly ComboBox quality = CreateSelector("Video quality");
        private readonly ComboBox format = CreateSelector("Video format");
        private readonly ComboBox captions = CreateSelector("Subtitles");
        private readonly CheckBox englishAudio = new CheckBox {
            Text = "Add English audio (synthetic voice)", AccessibleName = "Add English audio",
            AccessibleDescription = "Create a separate video with a spoken English track and the original audio.",
            AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty, ForeColor = StudioTheme.Ink
        };
        private readonly NumericUpDown parallelDownloads = new NumericUpDown {
            Minimum = 1, Maximum = AppPreferences.MaxParallelDownloads, Width = 54,
            AccessibleName = "Simultaneous downloads", Anchor = AnchorStyles.Left,
            TextAlign = HorizontalAlignment.Center, Margin = Padding.Empty
        };
        private readonly Label hint = CreateLabel("", Secondary);
        private readonly Label status = CreateLabel("Ready to download", TextColor);
        private readonly Label detail = CreateLabel("Your download will appear in the folder you choose.", Secondary);
        private readonly ProgressBar bar = new ProgressBar();
        private readonly Button download = CreateButton("Start downloads", true);
        private readonly Button cancel = CreateButton("Cancel", false);
        private readonly Button browse = CreateButton("Browse…", false);
        private readonly Button openFolder = CreateButton("Open folder", false);
        private readonly Button play = CreateButton("Play video", false);
        private readonly Button showDetails = CreateButton("Details", false);
        private readonly TabControl tabs = new WorkspacePages();
        private readonly TableLayoutPanel mainLayout = Grid(1, Padding.Empty);
        private bool fullScreen;
        private Rectangle previousBounds;
        private FormWindowState previousWindowState;
        private Control statusPanel;
        private int statusPage = -1;
        private readonly TextBox search = CreateInput("Search saved videos");
        private readonly ListView library = new ListView();
        private readonly Label libraryInfo = CreateLabel("Open the library to see your videos.", Secondary);
        private readonly Button libraryPlay = CreateButton("Play video", true);
        private readonly Button libraryFolder = CreateButton("Open location", false);
        private readonly Button libraryTranslate = CreateButton("Generate English subtitles", false);
        private readonly Button libraryDub = CreateButton("Create English audio", false);
        private readonly Button libraryRefresh = CreateButton("Refresh", false);
        private readonly System.Windows.Forms.Timer searchTimer = new System.Windows.Forms.Timer { Interval = 260 };
        private readonly List<string> operationDetails = new List<string>();

        private CancellationTokenSource activeDownload;
        private CancellationTokenSource libraryScan;
        private string operationTitle;
        private string savedFile;
        private bool closeAfterDownload;
        private bool historyWriteFailed;

        internal DownloaderForm() : this(AppPreferences.Load(), new WatchlistStore(SettingsDirectory),
            new DownloadHistoryStore(SettingsDirectory))
        {
        }

        internal DownloaderForm(AppPreferences preferences, WatchlistStore watchlistStore, DownloadHistoryStore historyStore)
        {
            this.preferences = preferences;
            this.historyStore = historyStore;
            Text = AppIdentity.Name + " " + AppIdentity.Version;
            AppIdentity.SetWindowIcon(this);
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10);
            BackColor = Background;
            ForeColor = TextColor;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1136, 730);
            MinimumSize = new Size(500, 390);

            quality.Items.AddRange(new object[] {
                "Best available", "Up to 4K (2160p)", "Up to 1440p", "Up to 1080p", "Up to 720p"
            });
            format.Items.AddRange(new object[] { "MKV · best quality", "MP4 · wider compatibility" });
            captions.Items.AddRange(new object[] {
                "None", "Original YouTube captions", "English subtitles", "Original + English"
            });
            quality.SelectedIndex = preferences.Quality;
            format.SelectedIndex = preferences.Format;
            captions.SelectedIndex = preferences.Captions;
            englishAudio.Checked = preferences.EnglishAudio;
            parallelDownloads.Value = preferences.ParallelDownloads;
            url.Multiline = true;
            url.AcceptsReturn = true;
            url.ScrollBars = ScrollBars.Vertical;
            folder.Text = preferences.Folder;
            folder.TextChanged += delegate {
                openFolder.Enabled = Directory.Exists(folder.Text);
                ScheduleLibraryRefresh();
            };
            openFolder.Enabled = Directory.Exists(folder.Text);
            cancel.Enabled = false;
            play.Enabled = false;
            showDetails.Enabled = false;

            var layout = mainLayout;
            layout.Padding = new Padding(22, 18, 22, 16);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowCount = layout.RowStyles.Count;
            tabs.Dock = DockStyle.Fill;
            tabs.Margin = Padding.Empty;
            var downloadTab = CreateWorkspaceTab("Download", CreateDownloadPanel(), new Size(342, 410));
            var libraryTab = CreateWorkspaceTab("Library", CreateLibraryPanel(), new Size(350, 270));
            tabs.TabPages.Add(downloadTab);
            tabs.TabPages.Add(libraryTab);
            watchlist = new WatchlistPanel(watchlistStore, FindWatchlistTitle, delegate { tabs.SelectedIndex = 2; });
            finder = new DiscoveryPanel(ToolsDirectory, AddDiscoveredVideo, watchlist.Add, preferences.CountryCode);
            var discoveryTab = CreateWorkspaceTab("Discover", finder, new Size(350, 390));
            tabs.TabPages.Add(discoveryTab);
            var watchlistTab = CreateWorkspaceTab("Watchlist", watchlist, new Size(350, 205));
            tabs.TabPages.Add(watchlistTab);
            history = new HistoryPanel(historyStore, RetryVideo, OpenPath, delegate { tabs.SelectedIndex = 0; });
            var historyTab = CreateWorkspaceTab("History", history, new Size(350, 255));
            tabs.TabPages.Add(historyTab);
            try { historyStore.RecoverInterrupted(); }
            catch (Exception ex)
            {
                if (!HistoryPanel.IsStorageError(ex))
                    throw;
                history.ReportStorageIssue("Couldn’t recover pending history. " + ex.Message);
            }
            layout.Controls.Add(tabs, 0, 1);
            statusPanel = CreateStatusPanel();
            layout.Controls.Add(statusPanel, 0, 2);
            ConfigureStudioShell();
            UpdateStatusVisibility();

            download.Click += DownloadClicked;
            cancel.Click += delegate {
                if (activeDownload == null)
                    return;
                cancel.Enabled = false;
                SetStatus("Cancelling…", "The current job will stop shortly.", false);
                activeDownload.Cancel();
            };
            browse.Click += BrowseClicked;
            openFolder.Click += delegate { OpenPath(folder.Text, true); };
            play.Click += delegate { OpenPath(savedFile, false); };
            showDetails.Click += delegate { ShowOperationDetails(); };
            format.SelectedIndexChanged += delegate { UpdateFormatHint(); };
            captions.SelectedIndexChanged += delegate { UpdateFormatHint(); };
            tabs.SelectedIndexChanged += delegate {
                ShowStudioPage();
                AcceptButton = tabs.SelectedIndex == 0 ? download : null;
                UpdateStatusVisibility();
                ScheduleLibraryRefresh();
                if (tabs.SelectedTab == watchlistTab)
                    watchlist.Reload();
                if (tabs.SelectedTab == historyTab)
                    history.Reload();
            };
            search.TextChanged += delegate { ScheduleLibraryRefresh(); };
            searchTimer.Tick += delegate {
                searchTimer.Stop();
                RefreshLibrary();
            };
            UpdateFormatHint();
            AcceptButton = download;
            ActiveControl = url;
            ResumeLayout(true);
        }

        private static TabPage CreateWorkspaceTab(string title, Control content, Size minimumSize)
        {
            var page = new TabPage(title) { BackColor = Background, AutoScroll = true, Padding = Padding.Empty };
            content.MinimumSize = minimumSize;
            page.AutoScrollMinSize = content.MinimumSize;
            content.SizeChanged += delegate {
                if (page.AutoScrollMinSize != content.MinimumSize)
                    page.AutoScrollMinSize = content.MinimumSize;
            };
            page.Controls.Add(content);
            return page;
        }

        protected override void OnLoad(EventArgs e)
        {
            var workArea = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, workArea.Width),
                Math.Min(MinimumSize.Height, workArea.Height));
            var size = new Size(Math.Min(Width, workArea.Width), Math.Min(Height, workArea.Height));
            Bounds = new Rectangle(workArea.Left + (workArea.Width - size.Width) / 2,
                workArea.Top + (workArea.Height - size.Height) / 2, size.Width, size.Height);
            base.OnLoad(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if ((keyData & Keys.Modifiers) == Keys.Control && key >= Keys.D1 && key <= Keys.D5)
            {
                tabs.SelectedIndex = (int)key - (int)Keys.D1;
                return true;
            }
            if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab))
            {
                int step = (keyData & Keys.Shift) == 0 ? 1 : -1;
                tabs.SelectedIndex = (tabs.SelectedIndex + tabs.TabCount + step) % tabs.TabCount;
                return true;
            }
            if (keyData == Keys.F11 || (keyData == Keys.Escape && fullScreen))
            {
                ToggleFullScreen();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ToggleFullScreen()
        {
            SuspendLayout();
            try
            {
                if (!fullScreen)
                {
                    var monitor = Screen.FromControl(this);
                    previousWindowState = WindowState;
                    previousBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                    WindowState = FormWindowState.Normal;
                    FormBorderStyle = FormBorderStyle.None;
                    Bounds = monitor.Bounds;
                }
                else
                {
                    WindowState = FormWindowState.Normal;
                    FormBorderStyle = FormBorderStyle.Sizable;
                    Bounds = previousBounds;
                    WindowState = previousWindowState;
                }
                fullScreen = !fullScreen;
                sidebar.SetFullScreen(fullScreen);
            }
            finally { ResumeLayout(true); }
        }



        private Control CreateStatusPanel()
        {
            var panel = Grid(2, new Padding(12, 8, 12, 8));
            panel.BackColor = StudioTheme.Surface;
            panel.Margin = new Padding(0, 8, 0, 0);
            panel.AutoSize = true;
            panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 9));
            panel.RowCount = panel.RowStyles.Count;

            status.Font = new Font("Segoe UI Semibold", 11);
            panel.Controls.Add(status, 0, 0);
            var commands = CreateActions(cancel, showDetails);
            commands.AutoSize = true;
            commands.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            panel.Controls.Add(commands, 1, 0);
            AddWide(panel, detail, 1);
            bar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            bar.Height = 5;
            bar.AccessibleName = "Download progress";
            bar.Margin = Padding.Empty;
            AddWide(panel, bar, 2);
            return panel;
        }


        private CollectionView libraryView;

        private Control CreateLibraryPanel()
        {
            var panel = Grid(2, Padding.Empty);
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowCount = panel.RowStyles.Count;
            panel.BackColor = StudioTheme.Canvas;
            AddWide(panel, CreateLabel("Search by title, category or series", StudioTheme.Ink), 0);
            search.Margin = new Padding(0, 0, 10, 0);
            panel.Controls.Add(search, 0, 1);
            libraryRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            libraryRefresh.Margin = Padding.Empty;
            panel.Controls.Add(libraryRefresh, 1, 1);
            library.AccessibleName = "Offline video library";
            library.Columns.Add("Video", 435);
            library.Columns.Add("Subtitles", 95);
            library.Columns.Add("Saved on", 110);
            libraryView = new CollectionView(library, CollectionKind.Library);
            libraryView.ShowMessage("Opening your library", "Looking for saved videos…");
            library.SelectedIndexChanged += delegate { UpdateLibraryButtons(); };
            library.ItemActivate += delegate {
                var video = SelectedVideo();
                if (video != null)
                    OpenPath(video.FilePath, false);
            };
            AddWide(panel, libraryView, 2);
            AddWide(panel, libraryInfo, 3);
            var actions = CollectionView.RowActions(libraryPlay, libraryFolder, libraryTranslate, libraryDub);
            AddWide(panel, actions, 4);
            actions.Visible = false;
            libraryPlay.Click += delegate {
                var video = SelectedVideo();
                if (video != null)
                    OpenPath(video.FilePath, false);
            };
            libraryFolder.Click += delegate {
                var video = SelectedVideo();
                if (video != null)
                    OpenPath(Path.GetDirectoryName(video.FilePath), true);
            };
            libraryTranslate.Click += GenerateEnglishClicked;
            libraryDub.Click += GenerateEnglishClicked;
            libraryRefresh.Click += delegate { RefreshLibrary(); };
            UpdateLibraryButtons();
            return panel;
        }

        private void ScheduleLibraryRefresh()
        {
            if (IsDisposed || Disposing || tabs.SelectedIndex != 1)
                return;
            searchTimer.Stop();
            searchTimer.Start();
        }

        private async void RefreshLibrary()
        {
            if (libraryScan != null)
                libraryScan.Cancel();
            var scan = new CancellationTokenSource();
            libraryScan = scan;
            string root = folder.Text.Trim();
            string filter = search.Text;
            if (library.Items.Count == 0)
                libraryView.ShowMessage("Opening your library", "Looking for saved videos…");
            libraryInfo.Text = library.Items.Count == 0 ? "" : "Looking for saved videos…";
            try
            {
                var videos = await Task.Run(() => VideoLibrary.Scan(root, filter, scan.Token), scan.Token);
                if (libraryScan != scan || IsDisposed || Disposing)
                    return;
                RenderLibrary(videos);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (libraryScan == scan && !IsDisposed && !Disposing)
                {
                    library.Items.Clear();
                    library.Groups.Clear();
                    libraryPlay.Parent.Visible = false;
                    libraryView.ShowMessage("Library unavailable", "Check the download folder or try scanning again.",
                        "Try again", RefreshLibrary);
                    libraryInfo.Text = ex.Message;
                    UpdateLibraryButtons();
                }
            }
            finally
            {
                if (libraryScan == scan)
                    libraryScan = null;
                scan.Dispose();
            }
        }

        private void RenderLibrary(List<LibraryVideo> videos)
        {
            videos.Sort((first, second) => {
                int order = StringComparer.OrdinalIgnoreCase.Compare(first.Category, second.Category);
                if (order == 0)
                    order = StringComparer.OrdinalIgnoreCase.Compare(first.Series, second.Series);
                if (order == 0)
                    order = StrCmpLogicalW(first.Name, second.Name);
                return order == 0 ? second.ModifiedAt.CompareTo(first.ModifiedAt) : order;
            });
            var selected = SelectedVideo();
            var groups = new Dictionary<string, ListViewGroup>(StringComparer.OrdinalIgnoreCase);
            library.BeginUpdate();
            try
            {
                library.Items.Clear();
                library.Groups.Clear();
                foreach (var video in videos)
                {
                    string label = String.IsNullOrWhiteSpace(video.Series) ? video.Category : video.Category + " / " + video.Series;
                    ListViewGroup group;
                    if (!groups.TryGetValue(label, out group))
                    {
                        group = new ListViewGroup(label);
                        groups.Add(label, group);
                        library.Groups.Add(group);
                    }
                    var item = new ListViewItem(new[] { video.Name, video.HasCaptions ? "Available" : "—", video.ModifiedAt.ToString("dd MMM yyyy") });
                    item.Group = group;
                    item.Tag = video;
                    libraryView.AddRow(item);
                    if (selected != null && String.Equals(selected.FilePath, video.FilePath, StringComparison.OrdinalIgnoreCase))
                        item.Selected = true;
                }
            }
            finally
            {
                library.EndUpdate();
            }
            if (videos.Count != 0)
                libraryView.ShowRows();
            else if (!String.IsNullOrWhiteSpace(search.Text))
                libraryView.ShowMessage("No matching videos", "Try another title, category or series.",
                    "Clear search", delegate { search.Clear(); search.Focus(); });
            else
                libraryView.ShowMessage("Your library is empty", "Videos saved in your download folder will appear here.",
                    "Start a download", delegate { tabs.SelectedIndex = 0; url.Focus(); });
            libraryPlay.Parent.Visible = videos.Count != 0;
            libraryInfo.Text = videos.Count == 0 ? "" :
                videos.Count + (videos.Count == 1 ? " video" : " videos") + " in " + groups.Count + (groups.Count == 1 ? " group" : " groups");
            UpdateLibraryButtons();
        }

        private LibraryVideo SelectedVideo()
        {
            return library.SelectedItems.Count == 1 ? library.SelectedItems[0].Tag as LibraryVideo : null;
        }

        private void UpdateLibraryButtons()
        {
            var video = SelectedVideo();
            bool selected = video != null;
            libraryPlay.Enabled = selected;
            libraryFolder.Enabled = selected;
            libraryTranslate.Enabled = selected && activeDownload == null;
            libraryDub.Enabled = selected && activeDownload == null && CanDubVideo(video.FilePath);
        }

        private static bool CanDubVideo(string file)
        {
            var ext = Path.GetExtension(file);
            return (String.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(ext, ".mkv", StringComparison.OrdinalIgnoreCase)) &&
                !Path.GetFileNameWithoutExtension(file).EndsWith(EnglishDubWorker.Suffix, StringComparison.OrdinalIgnoreCase);
        }

        private async void GenerateEnglishClicked(object sender, EventArgs e)
        {
            var video = SelectedVideo();
            if (video == null || activeDownload != null)
                return;
            bool dub = sender == libraryDub;
            var token = BeginOperation(dub ? "Creating English audio" : "Generating English subtitles");
            IProgress<DownloadProgress> updates = new Progress<DownloadProgress>(update => {
                if (activeDownload != null && activeDownload.Token == token)
                    UpdateProgress(update);
            });
            try
            {
                if (dub)
                {
                    savedFile = await EnglishDubWorker.RunAsync(ToolsDirectory(), null, video.FilePath, updates.Report, token);
                    operationDetails.Add("English audio saved in " + savedFile);
                    SetStatus("English audio ready", Path.GetFileName(savedFile), false);
                    FinishProgress(true);
                    return;
                }
                var result = await CaptionWorker.RunAsync(ToolsDirectory(), null, video.FilePath, CaptionMode.English, updates.Report, token);
                SetStatus(result.Count > 0 ? "English subtitles ready" : "No English subtitles saved", result.Message, result.Count == 0);
                FinishProgress(result.Count > 0);
            }
            catch (OperationCanceledException)
            {
                SetStatus(dub ? "English audio cancelled" : "Subtitle generation cancelled", video.Name, false);
                FinishProgress(false);
            }
            catch (Exception ex)
            {
                operationDetails.Add(ex.Message);
                SetStatus(dub ? "Couldn’t create English audio" : "Couldn’t generate English subtitles", ex.Message, true);
                FinishProgress(false);
            }
            finally
            {
                EndOperation();
                ScheduleLibraryRefresh();
            }
        }

        private sealed class DownloadLink
        {
            public string Url;
            public bool IsPlaylist;
        }

        private sealed class VideoJob
        {
            public string Url;
            public string Title;
            public int PlaylistPosition;
            public LibraryDestination Destination;
        }

        private sealed class DownloadBatch
        {
            public readonly List<VideoJob> Videos = new List<VideoJob>();
            public int FailedPlaylists;
            public int UnavailableEntries;
        }

        private sealed class BatchProgress
        {
            private sealed class Activity
            {
                public string Stage;
                public string Title;
                public string Message;
                public double? Percent;
            }

            private readonly DownloaderForm form;
            private readonly int total;
            private readonly CancellationToken token;
            private readonly SortedDictionary<int, Activity> active = new SortedDictionary<int, Activity>();
            private int finished;

            public BatchProgress(DownloaderForm form, int total, CancellationToken token)
            {
                this.form = form;
                this.total = total;
                this.token = token;
            }

            public IProgress<DownloadProgress> Stage(int index, string title, string stage, string message)
            {
                var activity = new Activity { Title = title, Stage = stage, Message = message };
                active[index] = activity;
                Render();
                return new Progress<DownloadProgress>(update => {
                    Activity current;
                    if (!active.TryGetValue(index, out current) || current != activity)
                        return;
                    activity.Message = update.Message;
                    activity.Percent = update.Percent;
                    Render();
                });
            }

            public void LeaveStage(int index, bool complete)
            {
                active.Remove(index);
                if (complete)
                    finished++;
                Render();
            }

            private void Render()
            {
                if (token.IsCancellationRequested || form.activeDownload == null || form.activeDownload.Token != token)
                    return;
                if (total == 1)
                {
                    foreach (var item in active.Values)
                    {
                        form.operationTitle = item.Stage + " · " + item.Title;
                        form.UpdateProgress(new DownloadProgress { Message = item.Message, Percent = item.Percent });
                    }
                    return;
                }
                var stages = new List<string>();
                foreach (var item in active)
                {
                    string state = item.Value.Stage;
                    if (item.Value.Percent.HasValue)
                        state += " " + Math.Max(0, Math.Min(100, item.Value.Percent.Value)).ToString("0") + "%";
                    stages.Add("#" + (item.Key + 1) + " " + state);
                }
                form.bar.Style = ProgressBarStyle.Continuous;
                form.bar.Value = 100 * finished / Math.Max(1, total);
                form.SetStatus(finished + " of " + total + " processed · " + active.Count + " active",
                    String.Join("   ·   ", stages.ToArray()), false);
            }
        }

        private async Task<DownloadBatch> ExpandLinksAsync(List<DownloadLink> links, string root, string categoryName,
            string seriesName, IProgress<DownloadProgress> updates, CancellationToken token)
        {
            var batch = new DownloadBatch();
            foreach (var link in links)
            {
                token.ThrowIfCancellationRequested();
                if (!link.IsPlaylist)
                {
                    batch.Videos.Add(new VideoJob { Url = link.Url });
                    continue;
                }

                try
                {
                    operationTitle = "Reading the playlist";
                    SetStatus(operationTitle, "Checking which videos are available.", false);
                    var playlist = await PlaylistWorker.ReadAsync(ToolsDirectory(), link.Url, token);
                    batch.UnavailableEntries += playlist.UnavailableCount;
                    operationDetails.Add(playlist.Title + " · " + playlist.Entries.Count + " available" +
                        (playlist.UnavailableCount > 0 ? ", " + playlist.UnavailableCount + " unavailable" : "") + ".");
                    if (playlist.Entries.Count == 0)
                        continue;

                    string groupName = String.IsNullOrWhiteSpace(seriesName) ? playlist.Title : seriesName;
                    var destination = await VideoLibrary.ChooseDestinationAsync(
                        ToolsDirectory(), playlist.Entries[0].VideoUrl, root, categoryName, groupName, updates.Report, token);
                    if (!String.IsNullOrWhiteSpace(destination.Warning))
                        operationDetails.Add(destination.Warning);
                    foreach (var entry in playlist.Entries)
                    {
                        batch.Videos.Add(new VideoJob {
                            Url = entry.VideoUrl, Title = entry.Title, PlaylistPosition = entry.Position, Destination = destination
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    batch.FailedPlaylists++;
                    operationDetails.Add("Couldn’t open playlist " + link.Url + Environment.NewLine + ex.Message);
                }
            }
            return batch;
        }

        private async void DownloadClicked(object sender, EventArgs e)
        {
            if (activeDownload != null)
                return;

            var links = new List<DownloadLink>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string root;
            try
            {
                string[] lines = url.Lines;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0)
                        continue;
                    try
                    {
                        string playlistUrl;
                        bool isPlaylist = PlaylistWorker.TryNormalizePlaylistUrl(line, out playlistUrl);
                        string canonical = isPlaylist ? playlistUrl : DownloadWorker.NormalizeVideoUrl(line);
                        if (seen.Add(canonical))
                            links.Add(new DownloadLink { Url = isPlaylist ? canonical : line, IsPlaylist = isPlaylist });
                    }
                    catch (ArgumentException ex)
                    {
                        throw new ArgumentException("Line " + (i + 1) + " needs a YouTube video link or a link from a playlist’s Share button.", ex);
                    }
                }
                if (links.Count == 0)
                    throw new ArgumentException("Paste at least one YouTube video or playlist link.");
                root = VideoLibrary.NormalizeRoot(folder.Text);
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException))
                    throw;
                SetStatus("Check your download details", ex.Message, true);
                return;
            }

            SavePreferences(root);
            var token = BeginOperation("Preparing downloads");
            var totals = new BatchTotals();
            bool expanding = true;
            IProgress<DownloadProgress> updates = new Progress<DownloadProgress>(update => {
                if (expanding && activeDownload != null && activeDownload.Token == token)
                    UpdateProgress(update);
            });
            try
            {
                var batch = await ExpandLinksAsync(links, root, category.Text, series.Text, updates, token);
                expanding = false;
                var entries = new List<DownloadHistoryEntry>();
                foreach (var job in batch.Videos)
                {
                    string canonical = DownloadWorker.NormalizeVideoUrl(job.Url);
                    string playlistId = VideoLibrary.GetPlaylistId(job.Url);
                    entries.Add(new DownloadHistoryEntry {
                        Id = Guid.NewGuid().ToString("N"), Url = canonical,
                        GroupingUrl = playlistId == null ? canonical : canonical + "&list=" + playlistId,
                        Title = job.Title ?? "YouTube video · " + canonical.Substring(canonical.Length - 11),
                        Root = root, RequestedCategory = category.Text, RequestedSeries = series.Text,
                        Destination = job.Destination == null ? null : job.Destination.DirectoryPath,
                        PlaylistPosition = job.PlaylistPosition, MaxHeight = QualityLimits[quality.SelectedIndex],
                        CompatibleMp4 = format.SelectedIndex == 1, Captions = (CaptionMode)captions.SelectedIndex,
                        EnglishAudio = englishAudio.Checked
                    });
                }

                await RunEntriesAsync(entries, (int)parallelDownloads.Value, totals, null, token);
                string summary = totals.Saved + (totals.Saved == 1 ? " video saved" : " videos saved");
                if (englishAudio.Checked)
                    summary += " · " + totals.Dubbed + " with English audio";
                if (captions.SelectedIndex != (int)CaptionMode.None)
                    summary += " · " + totals.Subtitles + (totals.Subtitles == 1 ? " subtitle file" : " subtitle files");
                if (totals.Failed > 0)
                    summary += " · " + totals.Failed + " failed";
                if (batch.UnavailableEntries > 0)
                    summary += " · " + batch.UnavailableEntries + " unavailable in playlists";
                if (batch.FailedPlaylists > 0)
                    summary += " · " + batch.FailedPlaylists + (batch.FailedPlaylists == 1 ? " playlist couldn’t open" : " playlists couldn’t open");
                if (totals.SubtitleIssues > 0)
                    summary += " · subtitle issues on " + totals.SubtitleIssues;
                if (totals.AudioIssues > 0)
                    summary += " · retry English audio for " + totals.AudioIssues + " in History";
                SetStatus(totals.Saved > 0 ? "Downloads finished" : "No videos downloaded", summary + ".", totals.Saved == 0);
                FinishProgress(totals.Saved > 0);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Download cancelled", totals.Saved == 0 ? "No videos finished." :
                    totals.Saved + " saved in your library" +
                    (englishAudio.Checked ? " · " + totals.Dubbed + " with English audio." : "."), false);
                FinishProgress(false);
            }
            catch (Exception ex)
            {
                operationDetails.Add(ex.Message);
                SetStatus("Couldn’t finish this batch", ex.Message, true);
                FinishProgress(false);
            }
            finally
            {
                expanding = false;
                EndOperation();
                ScheduleLibraryRefresh();
            }
        }

        private sealed class BatchTotals
        {
            public int Saved;
            public int Failed;
            public int Dubbed;
            public int Subtitles;
            public int SubtitleIssues;
            public int AudioIssues;
        }

        // The runner and the continuations below stay on the UI context; only the media workers run off-thread.
        private Task RunEntriesAsync(List<DownloadHistoryEntry> entries, int limit, BatchTotals totals,
            string recoveredSource, CancellationToken token)
        {
            var keys = entries.ConvertAll(entry => entry.Url);
            var progress = new BatchProgress(this, entries.Count, token);
            return DownloadBatchRunner.RunAsync(keys, limit,
                index => TransferEntryAsync(entries[index], index, recoveredSource, progress, totals, token),
                index => FinishEntryAsync(entries[index], index, progress, totals, token), token);
        }

        private async Task<bool> TransferEntryAsync(DownloadHistoryEntry entry, int index, string recoveredSource,
            BatchProgress display, BatchTotals totals, CancellationToken token)
        {
            entry.StartedUtc = DateTime.UtcNow;
            entry.State = HistoryState.Running;
            entry.FilePath = recoveredSource;
            entry.Note = "Preparing download.";
            RecordAttempt(entry);
            bool needsPostprocessing = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (String.IsNullOrWhiteSpace(entry.Destination))
                {
                    var updates = display.Stage(index, entry.Title, "Grouping", "Preparing the video folder.");
                    var destination = await VideoLibrary.ChooseDestinationAsync(ToolsDirectory(),
                        entry.GroupingUrl ?? entry.Url, entry.Root, entry.RequestedCategory, entry.RequestedSeries,
                        updates.Report, token);
                    if (!String.IsNullOrWhiteSpace(destination.Warning))
                        operationDetails.Add(entry.Title + ": " + destination.Warning);
                    entry.Destination = destination.DirectoryPath;
                    RecordAttempt(entry);
                }
                string target = VideoLibrary.ValidateRetryDestination(entry.Root, entry.Destination);
                if (recoveredSource == null)
                {
                    var updates = display.Stage(index, entry.Title, "Downloading", "Saving to " + target);
                    var result = await DownloadWorker.RunAsync(ToolsDirectory(), entry.Url, target,
                        entry.MaxHeight, entry.CompatibleMp4, updates.Report, token, entry.PlaylistPosition);
                    entry.FilePath = result.FilePath;
                }
                savedFile = entry.FilePath;
                if (entry.Title.StartsWith("YouTube video · ", StringComparison.Ordinal))
                    entry.Title = Path.GetFileNameWithoutExtension(entry.FilePath);
                totals.Saved++;
                operationDetails.Add((recoveredSource == null ? "Saved " : "Using saved original ") +
                    Path.GetFileName(entry.FilePath) + " in " + target + ".");
                needsPostprocessing = entry.EnglishAudio || entry.Captions != CaptionMode.None;
                entry.State = entry.EnglishAudio ? HistoryState.Running : HistoryState.Completed;
                entry.Note = entry.EnglishAudio ? "Original saved. English audio is pending." :
                    needsPostprocessing ? "Video saved. Subtitles may be incomplete." : "Video saved.";
                return needsPostprocessing;
            }
            catch (OperationCanceledException)
            {
                MarkCancelled(entry);
                throw;
            }
            catch (Exception ex)
            {
                entry.State = HistoryState.Failed;
                entry.Note = "Download failed. " + ex.Message;
                totals.Failed++;
                operationDetails.Add(entry.Title + ": " + entry.Note);
                return false;
            }
            finally
            {
                RecordAttempt(entry);
                display.LeaveStage(index, !needsPostprocessing);
            }
        }

        private async Task FinishEntryAsync(DownloadHistoryEntry entry, int index, BatchProgress display,
            BatchTotals totals, CancellationToken token)
        {
            string sourceFile = entry.FilePath;
            bool subtitleIssue = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (entry.Captions != CaptionMode.None)
                {
                    var updates = display.Stage(index, entry.Title, "Subtitles", Path.GetFileName(sourceFile));
                    try
                    {
                        var result = await CaptionWorker.RunAsync(ToolsDirectory(), entry.Url, sourceFile,
                            entry.Captions, updates.Report, token);
                        totals.Subtitles += result.Count;
                        subtitleIssue = result.MissingRequested;
                        operationDetails.Add(entry.Title + ": " + result.Message);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        subtitleIssue = true;
                        operationDetails.Add(entry.Title + " subtitle issue: " + ex.Message);
                    }
                    if (subtitleIssue)
                        totals.SubtitleIssues++;
                }
                if (entry.EnglishAudio)
                {
                    var updates = display.Stage(index, entry.Title, "English audio", Path.GetFileName(sourceFile));
                    string dubbed = await EnglishDubWorker.RunAsync(ToolsDirectory(), entry.Url, sourceFile,
                        updates.Report, token);
                    entry.FilePath = savedFile = dubbed;
                    totals.Dubbed++;
                    operationDetails.Add("English audio ready: " + Path.GetFileName(dubbed));
                }
                entry.State = HistoryState.Completed;
                entry.Note = entry.EnglishAudio ? "Video with English audio saved." : "Video saved.";
                if (entry.Captions != CaptionMode.None)
                    entry.Note += subtitleIssue ? " Some requested subtitles were unavailable." : " Requested subtitles are ready.";
            }
            catch (OperationCanceledException)
            {
                MarkCancelled(entry);
                throw;
            }
            catch (Exception ex)
            {
                entry.State = HistoryState.AudioPending;
                entry.Note = "Original saved. English audio could not be prepared. " + ex.Message;
                operationDetails.Add(entry.Title + ": " + entry.Note);
                totals.AudioIssues++;
            }
            finally
            {
                RecordAttempt(entry);
                display.LeaveStage(index, true);
            }
        }

        private static void MarkCancelled(DownloadHistoryEntry entry)
        {
            if (String.IsNullOrEmpty(entry.FilePath))
            {
                entry.State = HistoryState.Cancelled;
                entry.Note = "Cancelled. Retry to download this video.";
            }
            else if (entry.EnglishAudio)
            {
                entry.State = HistoryState.AudioPending;
                entry.Note = "Original saved. Retry English audio to finish this video.";
            }
            else
            {
                entry.State = HistoryState.Completed;
                entry.Note = "Video saved. Subtitle processing was cancelled.";
            }
        }

        private void RecordAttempt(DownloadHistoryEntry entry)
        {
            if (historyWriteFailed)
                return;
            try
            {
                historyStore.Upsert(entry);
                if (tabs.SelectedIndex == 4)
                    history.Reload();
            }
            catch (Exception ex)
            {
                if (!HistoryPanel.IsStorageError(ex))
                    throw;
                historyWriteFailed = true;
                operationDetails.Add("Couldn’t update download history. " + ex.Message);
                history.ReportStorageIssue("Couldn’t update download history. " + ex.Message);
            }
        }

        private async void RetryVideo(DownloadHistoryEntry entry)
        {
            if (activeDownload != null)
                return;
            string original = entry.State == HistoryState.AudioPending ? HistoryPanel.VideoFile(entry) : null;
            var token = BeginOperation("Retrying video");
            try
            {
                await RunEntriesAsync(new List<DownloadHistoryEntry> { entry }, 1, new BatchTotals(), original, token);
                bool complete = entry.State == HistoryState.Completed;
                SetStatus(complete ? "Ready to watch" : entry.State == HistoryState.AudioPending ?
                    "English audio needs attention" : "Download failed", entry.Note, !complete);
                FinishProgress(complete);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Retry cancelled", entry.Note, false);
                FinishProgress(entry.State == HistoryState.Completed);
            }
            catch (Exception ex)
            {
                operationDetails.Add(ex.Message);
                SetStatus("Couldn’t save this video", ex.Message, true);
                FinishProgress(false);
            }
            finally
            {
                EndOperation();
                ScheduleLibraryRefresh();
            }
        }

        private void UpdateProgress(DownloadProgress update)
        {
            if (activeDownload == null || activeDownload.IsCancellationRequested)
                return;
            if (update.Percent.HasValue)
            {
                bar.Style = ProgressBarStyle.Continuous;
                bar.Value = Math.Max(0, Math.Min(100, (int)update.Percent.Value));
            }
            else
            {
                bar.Style = ProgressBarStyle.Marquee;
            }
            SetStatus(operationTitle, update.Message, false);
        }

        private CancellationToken BeginOperation(string title)
        {
            operationDetails.Clear();
            historyWriteFailed = false;
            operationTitle = title;
            activeDownload = new CancellationTokenSource();
            SetWorking(true);
            SetStatus(title, "Getting things ready…", false);
            bar.Style = ProgressBarStyle.Marquee;
            return activeDownload.Token;
        }

        private void EndOperation()
        {
            activeDownload.Dispose();
            activeDownload = null;
            SetWorking(false);
            if (closeAfterDownload)
                Close();
        }

        private static string ToolsDirectory()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
        }

        private void FindWatchlistTitle(WatchlistEntry entry)
        {
            tabs.SelectedIndex = 2;
            finder.FindTitle(entry.Title, entry.Type);
        }

        private void AddDiscoveredVideo(string videoUrl)
        {
            if (activeDownload != null)
            {
                MessageBox.Show(this, "Finish or cancel the current job before adding another video.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string normalized = DownloadWorker.NormalizeVideoUrl(videoUrl);
            foreach (string line in url.Lines)
            {
                try
                {
                    if (DownloadWorker.NormalizeVideoUrl(line) == normalized)
                    {
                        tabs.SelectedIndex = 0;
                        url.Focus();
                        return;
                    }
                }
                catch (ArgumentException) { }
            }
            url.Text = String.IsNullOrWhiteSpace(url.Text) ? normalized : url.Text.TrimEnd() + Environment.NewLine + normalized;
            tabs.SelectedIndex = 0;
            url.Focus();
        }

        private void ShowOperationDetails()
        {
            using (var dialog = new Form {
                Text = "Video job details", ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent,
                AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
                ClientSize = new Size(590, 340), MinimizeBox = false, Font = Font,
                BackColor = StudioTheme.Canvas, ForeColor = StudioTheme.Ink
            })
            {
                AppIdentity.SetWindowIcon(dialog);
                var layout = Grid(1, new Padding(16));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
                layout.RowCount = 3;
                var title = CreateLabel("Job details", StudioTheme.Ink);
                title.Font = new Font("Georgia", 18);
                layout.Controls.Add(title, 0, 0);
                var log = new TextBox {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                    AccessibleName = "Job details", BackColor = StudioTheme.Surface, ForeColor = StudioTheme.Ink,
                    Dock = DockStyle.Fill, Text = String.Join(Environment.NewLine + Environment.NewLine, operationDetails)
                };
                layout.Controls.Add(log, 0, 1);
                var close = CreateButton("Close", false);
                close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
                close.DialogResult = DialogResult.OK;
                layout.Controls.Add(close, 0, 2);
                dialog.Controls.Add(layout);
                dialog.CancelButton = close;
                dialog.ShowDialog(this);
            }
        }

        private void SetWorking(bool working)
        {
            download.Enabled = !working;
            url.Enabled = !working;
            browse.Enabled = !working;
            folder.Enabled = !working;
            quality.Enabled = !working;
            format.Enabled = !working;
            captions.Enabled = !working;
            englishAudio.Enabled = !working;
            parallelDownloads.Enabled = !working;
            category.Enabled = !working;
            series.Enabled = !working;
            cancel.Enabled = working;
            play.Enabled = !working && File.Exists(savedFile);
            showDetails.Enabled = !working && operationDetails.Count > 0;
            openFolder.Enabled = Directory.Exists(folder.Text);
            UpdateLibraryButtons();
            history.SetBusy(working);
            UpdateStatusVisibility();
        }

        private void UpdateStatusVisibility()
        {
            cancel.Visible = activeDownload != null;
            showDetails.Visible = showDetails.Enabled;
            bar.Visible = activeDownload != null || bar.Value > 0;
            statusPanel.Visible = activeDownload != null || (statusPage >= 0 &&
                (tabs.SelectedIndex == statusPage || tabs.SelectedIndex == 0));
        }

        private void SetStatus(string title, string message, bool failed)
        {
            status.Text = title;
            status.ForeColor = failed ? Error : TextColor;
            detail.Text = message ?? "";
            statusPage = tabs.SelectedIndex;
            UpdateStatusVisibility();
        }

        private void FinishProgress(bool completed)
        {
            bar.Style = ProgressBarStyle.Blocks;
            bar.Value = completed ? 100 : 0;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (activeDownload != null)
            {
                e.Cancel = true;
                closeAfterDownload = true;
                cancel.Enabled = false;
                activeDownload.Cancel();
                SetStatus("Closing after cancellation…", "Stopping the current job.", false);
            }
            else
            {
                SavePreferences(AppPreferences.NormalizeFolder(folder.Text));
                searchTimer.Stop();
                searchTimer.Dispose();
                if (libraryScan != null)
                    libraryScan.Cancel();
            }
            base.OnFormClosing(e);
        }

        private void BrowseClicked(object sender, EventArgs e)
        {
            using (var picker = new FolderBrowserDialog {
                Description = "Choose where your offline videos will go.",
                SelectedPath = Directory.Exists(folder.Text) ? folder.Text : AppPreferences.DefaultFolder,
                ShowNewFolderButton = true
            })
            {
                if (picker.ShowDialog(this) == DialogResult.OK)
                    folder.Text = picker.SelectedPath;
            }
        }

        private void OpenPath(string path, bool isFolder)
        {
            if (isFolder ? !Directory.Exists(path) : !File.Exists(path))
            {
                SetStatus("Can’t find this " + (isFolder ? "folder" : "video"), path, true);
                return;
            }
            try
            {
                if (!isFolder && TryPlayWithSubtitles(path))
                    return;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                SetStatus("Couldn’t open this " + (isFolder ? "folder" : "video"), path, true);
            }
            catch (IOException ex)
            {
                SetStatus("Couldn’t open this " + (isFolder ? "folder" : "video"), ex.Message, true);
            }
            catch (UnauthorizedAccessException ex)
            {
                SetStatus("Couldn’t open this " + (isFolder ? "folder" : "video"), ex.Message, true);
            }
        }

        private static bool TryPlayWithSubtitles(string videoPath)
        {
            string subtitle = VideoLibrary.FindPreferredCaption(videoPath);
            if (subtitle == null)
                return false;

            var roots = new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (string root in roots)
            {
                if (String.IsNullOrEmpty(root))
                    continue;
                string vlc = Path.Combine(root, "VideoLAN", "VLC", "vlc.exe");
                if (!File.Exists(vlc))
                    continue;
                Process.Start(new ProcessStartInfo(vlc) {
                    Arguments = DownloadWorker.JoinArguments(new[] { "--sub-file", subtitle, videoPath }),
                    UseShellExecute = false
                });
                return true;
            }
            return false;
        }

        private void UpdateFormatHint()
        {
            hint.Text = format.SelectedIndex == 1
                ? "Uses H.264 and AAC when available. Some videos offer higher resolutions in MKV."
                : "Keeps the best available streams at your selected quality.";
        }

        private void SavePreferences(string location)
        {
            if (location != null)
                preferences.Folder = location;
            preferences.CountryCode = finder.CountryCode;
            preferences.Quality = quality.SelectedIndex;
            preferences.Format = format.SelectedIndex;
            preferences.Captions = captions.SelectedIndex;
            preferences.EnglishAudio = englishAudio.Checked;
            preferences.ParallelDownloads = Decimal.ToInt32(parallelDownloads.Value);
            preferences.Save();
        }

        internal static TableLayoutPanel Grid(int columns, Padding padding)
        {
            return new TableLayoutPanel {
                ColumnCount = columns, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = padding
            };
        }

        private static void AddWide(TableLayoutPanel grid, Control control, int row)
        {
            grid.Controls.Add(control, 0, row);
            grid.SetColumnSpan(control, grid.ColumnCount);
        }

        internal static FlowLayoutPanel CreateActions(params Button[] buttons)
        {
            var actions = new FlowLayoutPanel {
                Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty
            };
            actions.Controls.AddRange(buttons);
            return actions;
        }

        internal static TextBox CreateInput(string name)
        {
            return new TextBox {
                AccessibleName = name, BackColor = StudioTheme.Surface, ForeColor = TextColor,
                BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, Margin = Padding.Empty
            };
        }

        internal static ComboBox CreateSelector(string name)
        {
            var box = new ComboBox {
                AccessibleName = name, BackColor = StudioTheme.Surface, ForeColor = TextColor,
                FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill, Margin = Padding.Empty, DrawMode = DrawMode.OwnerDrawFixed
            };
            box.DrawItem += (sender, e) => {
                bool selected = (e.State & DrawItemState.Selected) != 0;
                bool highlighted = selected && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var brush = new SolidBrush(highlighted ? StudioTheme.Accent : StudioTheme.Surface))
                    e.Graphics.FillRectangle(brush, e.Bounds);
                if (e.Index >= 0)
                    TextRenderer.DrawText(e.Graphics, box.GetItemText(box.Items[e.Index]), box.Font, e.Bounds,
                        highlighted ? Color.White : TextColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPrefix);
                e.DrawFocusRectangle();
            };
            return box;
        }

        internal static Label CreateLabel(string text, Color color)
        {
            return new Label {
                Text = text, ForeColor = color, Dock = DockStyle.Fill, AutoEllipsis = true,
                Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false
            };
        }

        internal static Button CreateButton(string text, bool primary)
        {
            return new StudioButton(text, primary);
        }
    }
}
