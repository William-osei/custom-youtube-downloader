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

    internal sealed class DownloaderForm : Form
    {
        private static readonly Color Background = Color.FromArgb(15, 23, 39);
        private static readonly Color PanelBackground = Color.FromArgb(25, 37, 57);
        private static readonly Color InputBackground = Color.FromArgb(34, 49, 71);
        private static readonly Color TextColor = Color.FromArgb(240, 246, 254);
        private static readonly Color Secondary = Color.FromArgb(176, 191, 211);
        private static readonly Color Accent = Color.FromArgb(108, 229, 190);
        private static readonly Color Error = Color.FromArgb(255, 180, 177);
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
        private readonly TabControl tabs = new TabControl();
        private readonly TableLayoutPanel mainLayout = Grid(1, Padding.Empty);
        private readonly LinkLabel fullScreenLink = CreateHeaderLink("Full screen (F11)");
        private bool fullScreen;
        private Rectangle previousBounds;
        private FormWindowState previousWindowState;
        private Control statusPanel;
        private Control footer;
        private readonly TextBox search = CreateInput("Search saved videos");
        private readonly ListView library = new ListView();
        private readonly Label libraryInfo = CreateLabel("Open the library to see your videos.", Secondary);
        private readonly Button libraryPlay = CreateButton("Play video", true);
        private readonly Button libraryFolder = CreateButton("Open location", false);
        private readonly Button libraryTranslate = CreateButton("Generate English subtitles", false);
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
            ClientSize = new Size(896, 700);
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
            layout.Padding = new Padding(20, 12, 20, 10);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowCount = layout.RowStyles.Count;
            Controls.Add(layout);
            layout.Controls.Add(CreateHeader(), 0, 0);
            tabs.Dock = DockStyle.Fill;
            tabs.Margin = Padding.Empty;
            var downloadTab = CreateWorkspaceTab("Download", CreateDownloadPanel(), new Size(440, 408));
            var libraryTab = CreateWorkspaceTab("Library", CreateLibraryPanel(), new Size(590, 245));
            tabs.TabPages.Add(downloadTab);
            tabs.TabPages.Add(libraryTab);
            watchlist = new WatchlistPanel(watchlistStore, FindWatchlistTitle);
            finder = new DiscoveryPanel(ToolsDirectory, AddDiscoveredVideo, watchlist.Add, preferences.CountryCode);
            var discoveryTab = CreateWorkspaceTab("Find Movies & Series", finder, new Size(600, 435));
            tabs.TabPages.Add(discoveryTab);
            var watchlistTab = CreateWorkspaceTab("Watchlist", watchlist, new Size(470, 215));
            tabs.TabPages.Add(watchlistTab);
            history = new HistoryPanel(historyStore, RetryVideo, OpenPath);
            var historyTab = CreateWorkspaceTab("History", history, new Size(570, 240));
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
            footer = CreateFooter();
            layout.Controls.Add(statusPanel, 0, 2);
            layout.Controls.Add(footer, 0, 3);

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
            var page = new TabPage(title) { BackColor = PanelBackground, AutoScroll = true };
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
                fullScreenLink.Text = fullScreen ? "Exit full screen (F11)" : "Full screen (F11)";
            }
            finally { ResumeLayout(true); }
        }

        private Control CreateHeader()
        {
            var heading = Grid(4, new Padding(0, 0, 0, 5));
            heading.AutoSize = true;
            heading.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 59));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            heading.RowCount = 2;
            var brand = new PictureBox {
                Dock = DockStyle.Fill, Margin = new Padding(0, 3, 8, 3),
                SizeMode = PictureBoxSizeMode.Zoom, TabStop = false
            };
            Bitmap brandImage;
            using (var headerIcon = new Icon(Icon, 128, 128))
                brandImage = headerIcon.ToBitmap();
            brand.Image = brandImage;
            brand.Disposed += delegate { brandImage.Dispose(); };
            heading.Controls.Add(brand, 0, 0);
            heading.SetRowSpan(brand, 2);
            var title = CreateLabel("Custom YouTube Downloader", TextColor);
            title.Font = new Font("Segoe UI Semibold", 24);
            heading.Controls.Add(title, 1, 0);
            heading.SetColumnSpan(title, 3);
            heading.Controls.Add(CreateLabel("Find movies and series and manage your offline library.", Secondary), 1, 1);
            fullScreenLink.LinkClicked += delegate { ToggleFullScreen(); };
            heading.Controls.Add(fullScreenLink, 2, 1);
            var releases = CreateHeaderLink("Releases");
            releases.LinkClicked += delegate { AppIdentity.OpenReleases(this); };
            heading.Controls.Add(releases, 3, 1);
            return heading;
        }

        private static LinkLabel CreateHeaderLink(string text)
        {
            return new LinkLabel {
                Text = text, AutoSize = true, Anchor = AnchorStyles.Right,
                LinkColor = Accent, ActiveLinkColor = TextColor, VisitedLinkColor = Accent,
                Margin = new Padding(12, 0, 0, 0)
            };
        }

        private Control CreateDownloadPanel()
        {
            var card = Grid(2, new Padding(19, 14, 19, 11));
            card.BackColor = PanelBackground;
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 23, 32, 25, 31, 24, 24, 32, 24, 31, 24, 44 })
                card.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            card.RowCount = card.RowStyles.Count;

            AddWide(card, CreateLabel("YouTube video or playlist links · one per line", TextColor), 0);
            AddWide(card, url, 1);
            AddWide(card, CreateLabel("Save to", TextColor), 2);
            var folderRow = Grid(2, Padding.Empty);
            folderRow.RowCount = 1;
            folderRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            browse.AutoSize = false;
            browse.Dock = DockStyle.Fill;
            browse.Margin = new Padding(8, 0, 0, 3);
            folderRow.Controls.Add(folder, 0, 0);
            folderRow.Controls.Add(browse, 1, 0);
            AddWide(card, folderRow, 3);

            card.Controls.Add(CreateLabel("Quality", TextColor), 0, 4);
            card.Controls.Add(CreateLabel("File format", TextColor), 1, 4);
            quality.Margin = new Padding(0, 0, 8, 0);
            card.Controls.Add(quality, 0, 5);
            card.Controls.Add(format, 1, 5);
            AddWide(card, hint, 6);

            AddWide(card, CreateLabel("Subtitles", TextColor), 7);
            AddWide(card, captions, 8);
            card.Controls.Add(CreateLabel("Category · optional", TextColor), 0, 9);
            card.Controls.Add(CreateLabel("Series · optional", TextColor), 1, 9);
            category.Margin = new Padding(0, 0, 8, 0);
            card.Controls.Add(category, 0, 10);
            card.Controls.Add(series, 1, 10);
            AddWide(card, CreateLabel("Leave category or series blank to group automatically.", Secondary), 11);
            AddWide(card, CreateActions(download, openFolder, play), 12);
            return card;
        }

        private Control CreateStatusPanel()
        {
            var panel = Grid(2, new Padding(0, 9, 0, 0));
            panel.AutoSize = true;
            panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 15));
            panel.RowCount = panel.RowStyles.Count;

            status.Font = new Font("Segoe UI Semibold", 11);
            panel.Controls.Add(status, 0, 0);
            panel.Controls.Add(CreateActions(cancel, showDetails), 1, 0);
            AddWide(panel, detail, 1);
            bar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            bar.Height = 9;
            bar.AccessibleName = "Download progress";
            bar.Margin = Padding.Empty;
            AddWide(panel, bar, 2);
            return panel;
        }

        private static Control CreateFooter()
        {
            return CreateLabel("Download videos you own or have permission to save.", Secondary);
        }

        private Control CreateLibraryPanel()
        {
            var panel = Grid(1, new Padding(19, 14, 19, 9));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            panel.RowCount = panel.RowStyles.Count;
            panel.Controls.Add(CreateLabel("Find by title, category, or series", TextColor), 0, 0);
            panel.Controls.Add(search, 0, 1);
            library.Dock = DockStyle.Fill;
            library.Margin = Padding.Empty;
            library.AccessibleName = "Offline video library";
            library.View = View.Details;
            library.FullRowSelect = true;
            library.MultiSelect = false;
            library.HideSelection = false;
            library.BackColor = Color.FromArgb(245, 248, 253);
            library.ForeColor = Background;
            library.Columns.Add("Video", 435);
            library.Columns.Add("Subtitles", 95);
            library.Columns.Add("Saved on", 110);
            DownloadListLayout.FitTitleColumn(library);
            library.SelectedIndexChanged += delegate { UpdateLibraryButtons(); };
            library.ItemActivate += delegate {
                var video = SelectedVideo();
                if (video != null)
                    OpenPath(video.FilePath, false);
            };
            panel.Controls.Add(library, 0, 2);
            panel.Controls.Add(libraryInfo, 0, 3);
            panel.Controls.Add(CreateActions(libraryPlay, libraryFolder, libraryTranslate, libraryRefresh), 0, 4);
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
            libraryInfo.Text = "Looking for saved videos…";
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
                    libraryInfo.Text = "Couldn’t open this library: " + ex.Message;
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
                    library.Items.Add(item);
                    if (selected != null && String.Equals(selected.FilePath, video.FilePath, StringComparison.OrdinalIgnoreCase))
                        item.Selected = true;
                }
            }
            finally
            {
                library.EndUpdate();
            }
            libraryInfo.Text = videos.Count == 0 ? "No videos found in this folder." :
                videos.Count + (videos.Count == 1 ? " video" : " videos") + " in " + groups.Count + (groups.Count == 1 ? " group" : " groups");
            UpdateLibraryButtons();
        }

        private LibraryVideo SelectedVideo()
        {
            return library.SelectedItems.Count == 1 ? library.SelectedItems[0].Tag as LibraryVideo : null;
        }

        private void UpdateLibraryButtons()
        {
            bool selected = SelectedVideo() != null;
            libraryPlay.Enabled = selected;
            libraryFolder.Enabled = selected;
            libraryTranslate.Enabled = selected && activeDownload == null;
        }

        private async void GenerateEnglishClicked(object sender, EventArgs e)
        {
            var video = SelectedVideo();
            if (video == null || activeDownload != null)
                return;
            var token = BeginOperation("Generating English subtitles");
            IProgress<DownloadProgress> updates = new Progress<DownloadProgress>(UpdateProgress);
            try
            {
                var result = await CaptionWorker.RunAsync(ToolsDirectory(), null, video.FilePath, CaptionMode.English, updates.Report, token);
                SetStatus(result.Count > 0 ? "English subtitles ready" : "No English subtitles saved", result.Message, result.Count == 0);
                FinishProgress(result.Count > 0);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Subtitle generation cancelled", video.Name, false);
                FinishProgress(false);
            }
            catch (Exception ex)
            {
                operationDetails.Add(ex.Message);
                SetStatus("Couldn’t generate English subtitles", ex.Message, true);
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
            IProgress<DownloadProgress> updates = new Progress<DownloadProgress>(UpdateProgress);
            var captionMode = (CaptionMode)captions.SelectedIndex;
            int maxHeight = QualityLimits[quality.SelectedIndex];
            bool mp4 = format.SelectedIndex == 1;
            string categoryName = category.Text;
            string seriesName = series.Text;
            int saved = 0;
            int failed = 0;
            int captionFiles = 0;
            int captionWarnings = 0;
            DownloadHistoryEntry current = null;
            try
            {
                var batch = await ExpandLinksAsync(links, root, categoryName, seriesName, updates, token);
                var jobs = batch.Videos;
                for (int i = 0; i < jobs.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var job = jobs[i];
                    string prefix = jobs.Count == 1 ? "Video" : "Video " + (i + 1) + " of " + jobs.Count;
                    string canonical = DownloadWorker.NormalizeVideoUrl(job.Url);
                    string playlistId = VideoLibrary.GetPlaylistId(job.Url);
                    current = new DownloadHistoryEntry {
                        Id = Guid.NewGuid().ToString("N"), Url = canonical,
                        GroupingUrl = playlistId == null ? canonical : canonical + "&list=" + playlistId,
                        Title = job.Title ?? "YouTube video · " + canonical.Substring(canonical.Length - 11),
                        StartedUtc = DateTime.UtcNow, State = HistoryState.Running,
                        Root = root, RequestedCategory = categoryName, RequestedSeries = seriesName,
                        Destination = job.Destination == null ? null : job.Destination.DirectoryPath,
                        PlaylistPosition = job.PlaylistPosition, MaxHeight = maxHeight, CompatibleMp4 = mp4, Captions = captionMode
                    };
                    try
                    {
                        var result = await DownloadAttemptAsync(current, prefix, updates, token);
                        saved++;
                        captionFiles += result.Count;
                        if (result.MissingRequested)
                            captionWarnings++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        failed++;
                        operationDetails.Add(prefix + " failed: " + ex.Message);
                    }
                    current = null;
                }

                string summary = saved + (saved == 1 ? " video saved" : " videos saved");
                if (captionMode != CaptionMode.None)
                    summary += " · " + captionFiles + (captionFiles == 1 ? " subtitle file" : " subtitle files");
                if (failed > 0)
                    summary += " · " + failed + " failed";
                if (batch.UnavailableEntries > 0)
                    summary += " · " + batch.UnavailableEntries + " unavailable in playlists";
                if (batch.FailedPlaylists > 0)
                    summary += " · " + batch.FailedPlaylists + (batch.FailedPlaylists == 1 ? " playlist couldn’t open" : " playlists couldn’t open");
                if (captionWarnings > 0)
                    summary += " · subtitle issues on " + captionWarnings;
                SetStatus(saved > 0 ? "Ready to watch" : "No videos downloaded", summary + ".", saved == 0);
                FinishProgress(saved > 0);
            }
            catch (OperationCanceledException)
            {
                if (current != null && current.State == HistoryState.Completed)
                    saved++;
                SetStatus("Download cancelled", saved == 0 ? "No videos finished." : saved + " finished video(s) are in your library.", false);
                FinishProgress(false);
            }
            finally
            {
                EndOperation();
                ScheduleLibraryRefresh();
            }
        }

        private async Task<CaptionResult> DownloadAttemptAsync(DownloadHistoryEntry entry, string prefix,
            IProgress<DownloadProgress> updates, CancellationToken token)
        {
            RecordAttempt(entry);
            try
            {
                if (String.IsNullOrWhiteSpace(entry.Destination))
                {
                    operationTitle = prefix + " · finding its category and series";
                    SetStatus(operationTitle, "Preparing a folder for this video.", false);
                    var destination = await VideoLibrary.ChooseDestinationAsync(ToolsDirectory(),
                        entry.GroupingUrl ?? entry.Url, entry.Root, entry.RequestedCategory, entry.RequestedSeries,
                        updates.Report, token);
                    if (!String.IsNullOrWhiteSpace(destination.Warning))
                        operationDetails.Add(prefix + ": " + destination.Warning);
                    entry.Destination = destination.DirectoryPath;
                    RecordAttempt(entry);
                }
                string target = VideoLibrary.ValidateRetryDestination(entry.Root, entry.Destination);
                operationTitle = prefix + " · downloading";
                SetStatus(operationTitle, "Saving to " + target, false);
                var result = await DownloadWorker.RunAsync(ToolsDirectory(), entry.Url, target,
                    entry.MaxHeight, entry.CompatibleMp4, updates.Report, token, entry.PlaylistPosition);
                savedFile = entry.FilePath = result.FilePath;
                if (entry.Title.StartsWith("YouTube video · ", StringComparison.Ordinal))
                    entry.Title = Path.GetFileNameWithoutExtension(savedFile);
                entry.State = HistoryState.Completed;
                entry.Note = "Video saved.";
                operationDetails.Add("Saved " + Path.GetFileName(savedFile) + " in " + target + ".");
                if (entry.Captions == CaptionMode.None)
                    return new CaptionResult();

                entry.Note = "Video saved. Subtitles may be incomplete.";
                RecordAttempt(entry);
                operationTitle = prefix + " · preparing subtitles";
                SetStatus(operationTitle, Path.GetFileName(savedFile), false);
                try
                {
                    var captions = await CaptionWorker.RunAsync(ToolsDirectory(), entry.Url, savedFile,
                        entry.Captions, updates.Report, token);
                    entry.Note = captions.MissingRequested ? "Video saved. Some requested subtitles were unavailable." :
                        "Video and requested subtitles saved.";
                    operationDetails.Add(captions.Message);
                    return captions;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    entry.Note = "Video saved. Subtitles could not be prepared.";
                    operationDetails.Add(prefix + " subtitle issue: " + ex.Message);
                    return new CaptionResult { MissingRequested = true };
                }
            }
            catch (OperationCanceledException)
            {
                if (entry.State == HistoryState.Completed)
                    entry.Note = "Video saved. Subtitle processing was cancelled.";
                else
                {
                    entry.State = HistoryState.Cancelled;
                    entry.Note = "Cancelled. Retry to try this video again.";
                }
                throw;
            }
            catch (Exception)
            {
                entry.State = HistoryState.Failed;
                entry.Note = "The video could not be saved. Retry uses the same settings.";
                throw;
            }
            finally { RecordAttempt(entry); }
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
            var token = BeginOperation("Retrying video");
            IProgress<DownloadProgress> updates = new Progress<DownloadProgress>(UpdateProgress);
            entry.State = HistoryState.Running;
            entry.StartedUtc = DateTime.UtcNow;
            entry.FilePath = null;
            entry.Note = "Retry started.";
            try
            {
                var captions = await DownloadAttemptAsync(entry, "Retry", updates, token);
                SetStatus("Ready to watch", captions.MissingRequested ? entry.Note : Path.GetFileName(entry.FilePath), false);
                FinishProgress(true);
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
                ClientSize = new Size(680, 340), MinimizeBox = false, Font = Font
            })
            {
                AppIdentity.SetWindowIcon(dialog);
                var log = new TextBox {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                    Dock = DockStyle.Fill, Text = String.Join(Environment.NewLine + Environment.NewLine, operationDetails)
                };
                dialog.Controls.Add(log);
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
            bool finding = tabs.SelectedIndex == 2 || tabs.SelectedIndex == 3;
            bool showJob = !finding || activeDownload != null;
            statusPanel.Visible = showJob;
            footer.Visible = !finding;
        }

        private void SetStatus(string title, string message, bool failed)
        {
            status.Text = title;
            status.ForeColor = failed ? Error : TextColor;
            detail.Text = message ?? "";
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
                AccessibleName = name, BackColor = InputBackground, ForeColor = TextColor,
                BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, Margin = Padding.Empty
            };
        }

        internal static ComboBox CreateSelector(string name)
        {
            var box = new ComboBox {
                AccessibleName = name, BackColor = InputBackground, ForeColor = TextColor,
                FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill, Margin = Padding.Empty, DrawMode = DrawMode.OwnerDrawFixed
            };
            box.DrawItem += (sender, e) => {
                bool selected = (e.State & DrawItemState.Selected) != 0;
                using (var brush = new SolidBrush(selected ? Accent : InputBackground))
                    e.Graphics.FillRectangle(brush, e.Bounds);
                if (e.Index >= 0)
                    TextRenderer.DrawText(e.Graphics, box.GetItemText(box.Items[e.Index]), box.Font, e.Bounds,
                        selected ? Background : TextColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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
            return new ActionButton {
                Text = text, FlatStyle = FlatStyle.Flat, ForeColor = primary ? Background : TextColor,
                BackColor = primary ? Accent : InputBackground, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 8, 0), Padding = new Padding(10, 0, 10, 0), UseVisualStyleBackColor = false
            };
        }

        private sealed class ActionButton : Button
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                var bounds = ClientRectangle;
                using (var background = new SolidBrush(Enabled ? BackColor : PanelBackground))
                    e.Graphics.FillRectangle(background, bounds);
                if (BackColor == InputBackground || !Enabled)
                {
                    using (var outline = new Pen(Color.FromArgb(71, 92, 119)))
                        e.Graphics.DrawRectangle(outline, 0, 0, bounds.Width - 1, bounds.Height - 1);
                }
                TextRenderer.DrawText(e.Graphics, Text, Font, bounds,
                    Enabled ? ForeColor : Secondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (Focused && ShowFocusCues)
                    ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -4, -4));
            }
        }
    }
}
