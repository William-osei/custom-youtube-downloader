using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;

namespace CustomYouTubeDownloader;

internal sealed partial class StudioWindow : Window
{
    private static readonly IBrush Forest = Brush.Parse("#16382E"), Pine = Brush.Parse("#195D48"),
        Ink = Brush.Parse("#18372E"), Muted = Brush.Parse("#526A60"), Paper = Brush.Parse("#F3F5F0"),
        Line = Brush.Parse("#D2DCD3"), Pale = Brush.Parse("#DBEFDF");
    private static readonly string[] Sections = { "Download", "Library", "Discover", "Watchlist", "History" };
    private static readonly string[] Voices = { "Faithful translation", "Serious storyteller", "Sarcastic storyteller" };
    private readonly string tools = PlatformTools.ToolsDirectory;
    private readonly AppPreferences preferences;
    private readonly PortableDownloads batch;
    private readonly DownloadHistoryStore history;
    private readonly WatchlistStore watchlist;
    private readonly ContentControl content = new();
    private readonly Button[] navigation = new Button[Sections.Length];
    private readonly Control[] pages = new Control[Sections.Length];
    private readonly TextBlock status = new() { Text = "Ready", TextWrapping = TextWrapping.Wrap, Foreground = Ink, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar activity = new() { Height = 3, IsIndeterminate = true, IsVisible = false, Foreground = Pine };
    private readonly Button stop = new() { Content = "Cancel", IsVisible = false };
    private readonly TextBox urls = new() { AcceptsReturn = true, Height = 74, TextWrapping = TextWrapping.Wrap,
        Watermark = "Paste a YouTube video or playlist link, one per line" };
    private readonly TextBox folder = new(), category = new() { Watermark = "Automatic" }, series = new() { Watermark = "Automatic" };
    private readonly ComboBox quality = Selector("Best available", "Up to 2160p", "Up to 1440p", "Up to 1080p", "Up to 720p");
    private readonly ComboBox format = Selector("MKV · best streams", "MP4 · compatible");
    private readonly ComboBox captions = Selector("None", "Original captions", "English subtitles", "Original + English");
    private readonly ComboBox limit = Selector("1", "2", "3", "4");
    private readonly ComboBox style = Selector(Voices), libraryStyle = Selector(Voices);
    private readonly CheckBox dub = new() { Content = "Add English audio", VerticalContentAlignment = VerticalAlignment.Center };
    private readonly List<Button> taskButtons = new();
    private readonly List<Control> downloadSettings = new();
    private CancellationTokenSource job;
    private bool closing, closed;
    private int selectedPage;
    private WindowState previousWindowState;
    private Bitmap masthead, iconArtwork;

    internal StudioWindow(string profile)
    {
        preferences = AppPreferences.Load(profile);
        if (Program.SmokeTest) preferences.Folder = Path.Combine(profile, "Videos");
        history = new DownloadHistoryStore(profile);
        watchlist = new WatchlistStore(profile);
        batch = new PortableDownloads(tools, history);
        Title = "Custom YouTube Downloader " + Program.Version;
        MinWidth = 680; MinHeight = 480;
        var monitor = Screens.Primary;
        Width = monitor == null ? 1030 : Math.Clamp(monitor.WorkingArea.Width / monitor.Scaling - 24, MinWidth, 1030);
        Height = monitor == null ? 700 : Math.Clamp(monitor.WorkingArea.Height / monitor.Scaling - 44, MinHeight, 760);
        Background = Paper; Foreground = Ink;
        using (var image = Artwork("app-icon.png")) Icon = new WindowIcon(image);
        using (var image = Artwork("app-icon.png")) iconArtwork = new Bitmap(image);
        using (var image = Artwork("film-reel.jpg")) masthead = new Bitmap(image);
        folder.Text = preferences.Folder; quality.SelectedIndex = preferences.Quality; format.SelectedIndex = preferences.Format;
        captions.SelectedIndex = preferences.Captions; limit.SelectedIndex = preferences.ParallelDownloads - 1;
        dub.IsChecked = preferences.EnglishAudio; style.SelectedIndex = libraryStyle.SelectedIndex = (int)preferences.AudioStyle;
        style.IsEnabled = dub.IsChecked == true;
        dub.IsCheckedChanged += (_, _) => style.IsEnabled = dub.IsChecked == true;
        style.SelectionChanged += (_, _) => libraryStyle.SelectedIndex = style.SelectedIndex;
        libraryStyle.SelectionChanged += (_, _) => style.SelectedIndex = libraryStyle.SelectedIndex;
        pages[0] = DownloadPage(); pages[1] = LibraryPage(); pages[2] = DiscoverPage();
        pages[3] = WatchlistPage(); pages[4] = HistoryPage();
        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), RowDefinitions = new RowDefinitions("*") };
        var rail = new StackPanel { Margin = new Thickness(12, 22), Spacing = 6 };
        rail.Children.Add(new Image { Source = iconArtwork, Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(7, 0, 0, 2) });
        rail.Children.Add(Text("Custom YouTube\nDownloader", 16, Brushes.White, true));
        for (int i = 0; i < Sections.Length; i++)
        {
            int index = i;
            var button = new Button { Content = Sections[i], CornerRadius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 9), BorderThickness = new Thickness(0),
                Margin = new Thickness(0, i == 0 ? 18 : 0, 0, 0) };
            ToolTip.SetTip(button, Sections[i] + " · " + (OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+") + (i + 1));
            button.Click += (_, _) => Navigate(index);
            rail.Children.Add(navigation[i] = button);
        }
        shell.Children.Add(new Border { Background = Forest, Child = rail });
        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("*") };
        Place(shell, body, 1, 0);
        Place(body, content, 0, 0);
        Place(body, activity, 0, 1);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        status.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(status, "Operation status");
        Place(footer, status, 0, 0); Place(footer, stop, 1, 0);
        stop.Click += (_, _) => { job?.Cancel(); stop.IsEnabled = false; Report("Cancelling…"); };
        Place(body, new Border { Child = footer, Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 9), MinHeight = 40 }, 0, 2);
        Content = shell;
        Navigate(0);
        Opened += (_, _) => {
            // A window manager may clamp the initial request to the current monitor's work area.
            if (Width > ClientSize.Width && ClientSize.Width >= MinWidth) Width = ClientSize.Width;
            if (Height > ClientSize.Height && ClientSize.Height >= MinHeight) Height = ClientSize.Height;
        };
        try { history.RecoverInterrupted(); } catch (Exception ex) { Report(ex.Message); }
        Closing += (_, e) => {
            if (job != null) { e.Cancel = true; closing = true; job.Cancel(); Report("Finishing the current operation…"); return; }
            SaveSettings();
        };
        Closed += (_, _) => { closed = true; libraryScan?.Cancel(); discovery?.Cancel(); iconArtwork.Dispose(); masthead.Dispose(); };
        KeyDown += (_, e) => {
            if (e.Key == Key.F11) {
                if (WindowState == WindowState.FullScreen) WindowState = previousWindowState;
                else { previousWindowState = WindowState; WindowState = WindowState.FullScreen; }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && WindowState == WindowState.FullScreen) { WindowState = previousWindowState; e.Handled = true; }
            else if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                int direction = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
                Navigate((selectedPage + direction + pages.Length) % pages.Length);
                e.Handled = true;
            }
            else if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && e.Key >= Key.D1 && e.Key <= Key.D5)
            { Navigate((int)e.Key - (int)Key.D1); e.Handled = true; }
        };
    }

    private static Stream Artwork(string file) => AssetLoader.Open(new Uri("avares://CustomYouTubeDownloader/Assets/" + file));
    private static TextBlock Text(string value, double size, IBrush color, bool bold = false) => new() {
        Text = value, FontSize = size, Foreground = color, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        TextWrapping = TextWrapping.Wrap };
    private static ComboBox Selector(params string[] options) => new() {
        ItemsSource = options, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
    private static void Place(Grid grid, Control child, int col, int row)
    { Grid.SetColumn(child, col); Grid.SetRow(child, row); grid.Children.Add(child); }
    private static Control Field(string label, Control field)
    {
        AutomationProperties.SetName(field, label);
        return new StackPanel { Spacing = 5, Children = { Text(label, 13, Muted, true), field } };
    }
    private static Border Card(Control contents) => new() { Padding = new Thickness(16), Background = Brushes.White,
        BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = contents };
    private Button ActionButton(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
        if (primary) { button.Background = Pine; button.Foreground = Brushes.White; button.FontWeight = FontWeight.SemiBold; }
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { Report(ex.Message); } };
        return button;
    }
    private static Control Pair(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        Place(grid, left, 0, 0); Place(grid, right, 1, 0); return grid;
    }
    private Button TaskButton(string label, Func<CancellationToken, IProgress<string>, Task<string>> work, bool primary = false)
    {
        var button = ActionButton(label, () => _ = RunAsync(work), primary);
        taskButtons.Add(button); return button;
    }
    private async Task RunAsync(Func<CancellationToken, IProgress<string>, Task<string>> work)
    {
        if (job != null) return;
        using var operation = new CancellationTokenSource();
        job = operation;
        activity.IsVisible = stop.IsVisible = stop.IsEnabled = true;
        foreach (var item in taskButtons) item.IsEnabled = false;
        foreach (var item in downloadSettings) item.IsEnabled = false;
        RefreshActions();
        try
        {
            var updates = new Progress<string>(s => { if (job == operation && !operation.IsCancellationRequested) Report(s); });
            Report("Preparing…");
            string result = await work(operation.Token, updates);
            Report(result);
        }
        catch (OperationCanceledException) { Report("Cancelled. Finished videos are available in Library."); }
        catch (Exception ex) { Report(ex.Message); }
        finally
        {
            job = null;
            foreach (var item in taskButtons) item.IsEnabled = true;
            foreach (var item in downloadSettings) item.IsEnabled = true;
            style.IsEnabled = dub.IsChecked == true;
            activity.IsVisible = stop.IsVisible = false;
            RefreshActions();
            if (closing) Close();
            else if (selectedPage == 1) await RefreshLibraryAsync();
            else if (selectedPage == 4) RefreshHistory();
        }
    }
    private void Report(string message)
    {
        status.Text = message;
        ToolTip.SetTip(status, message);
    }
    private void SaveSettings()
    {
        if (AppPreferences.NormalizeFolder(folder.Text) is string path) preferences.Folder = path;
        preferences.Quality = quality.SelectedIndex; preferences.Format = format.SelectedIndex;
        preferences.Captions = captions.SelectedIndex; preferences.EnglishAudio = dub.IsChecked == true;
        preferences.AudioStyle = (NarrationStyle)style.SelectedIndex; preferences.ParallelDownloads = limit.SelectedIndex + 1;
        preferences.CountryCode = Countries[country.SelectedIndex];
        preferences.Save();
    }
    private void Navigate(int index)
    {
        selectedPage = index;
        for (int i = 0; i < navigation.Length; i++)
        {
            navigation[i].Background = i == index ? Pale : Brushes.Transparent;
            navigation[i].Foreground = i == index ? Ink : Brushes.White;
        }
        content.Content = pages[index];
        if (index == 1) _ = RefreshLibraryAsync();
        if (index == 3) RefreshWatchlist();
        if (index == 4) RefreshHistory();
    }
    internal async Task ExerciseWorkspacesAsync()
    {
        for (int i = 0; i < pages.Length; i++)
        {
            Navigate(i);
            var page = pages[i];
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                var footer = status.TranslatePoint(new Point(0, status.Bounds.Height), this);
                if (content.Content == page && TopLevel.GetTopLevel(page) == this && IsVisible &&
                    page.IsArrangeValid && page.Bounds.Width >= 100 && page.Bounds.Height >= 100 &&
                    status.IsArrangeValid && status.Bounds.Height > 0 && footer.HasValue &&
                    footer.Value.Y >= status.Bounds.Height && footer.Value.Y <= ClientSize.Height + 1)
                    break;

                if (elapsed.Elapsed >= TimeSpan.FromSeconds(30))
                    throw new InvalidOperationException(
                        $"{Sections[i]} did not finish layout within 30 seconds. Page bounds: {page.Bounds}, " +
                        $"window: {ClientSize}, status bottom: {footer?.Y}.");
                await Task.Delay(50);
            }
        }
        Navigate(0);
    }

    private Control DownloadPage()
    {
        var fields = new StackPanel { Spacing = 13 };
        var banner = new Panel { Height = 88, ClipToBounds = true, Background = Forest };
        banner.Children.Add(new Image { Source = masthead, Stretch = Stretch.UniformToFill });
        banner.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(22, 0), Child = Text("Download studio", 29, Brushes.White, true) });
        fields.Children.Add(banner);
        var paste = ActionButton("Paste links", async () => {
            try
            {
                string value = Clipboard == null ? null : await Clipboard.TryGetTextAsync();
                if (!string.IsNullOrWhiteSpace(value)) urls.Text = string.IsNullOrWhiteSpace(urls.Text) ? value.Trim() : urls.Text.TrimEnd() + "\n" + value.Trim();
            } catch (Exception ex) { Report(ex.Message); }
        });
        var urlHeading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Place(urlHeading, Text("Video or playlist links", 17, Ink, true), 0, 0); Place(urlHeading, paste, 1, 0);
        AutomationProperties.SetName(urls, "Video or playlist links");
        fields.Children.Add(Card(new StackPanel { Spacing = 8, Children = { urlHeading, urls } }));

        var videoSettings = new StackPanel { Spacing = 10 };
        videoSettings.Children.Add(Text("Video settings", 17, Ink, true));
        videoSettings.Children.Add(Pair(Field("Quality", quality), Field("Format", format)));
        videoSettings.Children.Add(Pair(Field("Subtitles", captions), Field("Simultaneous downloads", limit)));
        videoSettings.Children.Add(Pair(dub, Field("Audio style", style)));
        var destination = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Place(destination, folder, 0, 0);
        var choose = ActionButton("Choose folder", () => _ = ChooseFolderAsync());
        Place(destination, choose, 1, 0);
        AutomationProperties.SetName(folder, "Download folder");
        var folderCard = Card(new StackPanel { Spacing = 10, Children = {
            Text("Download folder", 17, Ink, true), destination, Field("Category", category), Field("Series", series)
        } });
        var settings = new Grid { ColumnSpacing = 12, RowSpacing = 12, ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto") };
        Place(settings, Card(videoSettings), 0, 0); Place(settings, folderCard, 1, 0);
        settings.SizeChanged += (_, e) => {
            bool stack = e.NewSize.Width < 720;
            if (stack == (settings.ColumnDefinitions.Count == 1)) return;
            settings.ColumnDefinitions = new ColumnDefinitions(stack ? "*" : "*,*");
            settings.RowDefinitions = new RowDefinitions(stack ? "Auto,Auto" : "Auto");
            Grid.SetColumn(folderCard, stack ? 0 : 1); Grid.SetRow(folderCard, stack ? 1 : 0);
        };
        fields.Children.Add(settings);
        downloadSettings.AddRange(new Control[] { urls, paste, videoSettings, destination, category, series });
        var scroll = new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18, 16, 18, 4) };
        var start = TaskButton("Start downloads", (token, updates) => {
            VideoLibrary.NormalizeRoot(folder.Text);
            SaveSettings();
            return batch.DownloadAsync(urls.Text ?? "", preferences, category.Text, series.Text, updates, token);
        }, true);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Place(layout, scroll, 0, 0);
        Place(layout, new Border { Padding = new Thickness(18, 9), Child = start, HorizontalAlignment = HorizontalAlignment.Right }, 0, 1);
        return layout;
    }

    private async Task ChooseFolderAsync()
    {
        try
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose download folder" });
            if (result.FirstOrDefault()?.TryGetLocalPath() is string path) { folder.Text = path; SaveSettings(); }
        } catch (Exception ex) { Report(ex.Message); }
    }
}
