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
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace CustomYouTubeDownloader;

internal sealed partial class StudioWindow
{
    private static readonly string[] Countries = { "GH", "US", "GB", "CA", "NG", "ZA", "IN", "DE", "FR", "AU" };
    private readonly ComboBox country = Selector("Ghana", "United States", "United Kingdom", "Canada", "Nigeria",
        "South Africa", "India", "Germany", "France", "Australia");
    private readonly ComboBox kind = Selector("Movies and series", "Movies", "TV series");
    private readonly TextBox filter = new() { Watermark = "Search your library" }, query = new() { Watermark = "Movie or series title" };
    private readonly ListBox library = new(), results = new(), saved = new(), attempts = new();
    private readonly TextBlock libraryEmpty = Empty(), searchEmpty = Empty(), savedEmpty = Empty(), historyEmpty = Empty();
    private Button play, reveal, subtitles, narrate, review, browse, bookmark, addLink, visit, remove, retry, replay, find;
    private CancellationTokenSource libraryScan, discovery;
    private static TextBlock Empty() => new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 400,
        TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    private static void FormatRows<T>(ListBox list, Func<T, string> title, Func<T, string> detail) where T : class
    {
        list.ItemTemplate = new FuncDataTemplate<T>((item, _) => {
            var heading = Text(title(item), 15, Ink, true);
            var description = Text(detail(item), 12, Muted);
            description.TextTrimming = TextTrimming.CharacterEllipsis;
            description.MaxLines = 2;
            return new StackPanel { Spacing = 3, Margin = new Thickness(3, 4), Children = { heading, description } };
        });
    }

    private static Control CollectionPage(string heading, Control toolbar, ListBox list, TextBlock message, Control actions)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 12, Margin = new Thickness(20, 18) };
        Place(grid, Text(heading, 28, Ink, true), 0, 0);
        Place(grid, toolbar, 0, 1);
        list.BorderThickness = new Thickness(0); list.Background = Brushes.White;
        AutomationProperties.SetName(list, heading);
        var surface = Card(new Panel { Children = { list, message } }); surface.Padding = new Thickness(6);
        Place(grid, surface, 0, 2); Place(grid, actions, 0, 3); return grid;
    }

    private static WrapPanel Buttons(params Control[] controls)
    {
        var panel = new WrapPanel();
        foreach (var child in controls) { child.Margin = new Thickness(0, 0, 7, 5); panel.Children.Add(child); }
        return panel;
    }
    private static string Joined(params string[] parts) => string.Join("  ·  ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
    private static void Items<T>(ListBox list, TextBlock overlay, IReadOnlyList<T> items, string emptyMessage)
    {
        list.ItemsSource = items;
        overlay.Text = items.Count == 0 ? emptyMessage : "";
        overlay.IsVisible = items.Count == 0;
    }
    private static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            throw new FileNotFoundException("The file or folder is no longer available.");
        using var launched = Process.Start(new ProcessStartInfo { FileName = Path.GetFullPath(path), UseShellExecute = true });
    }
    private static void OpenLink(string url)
    {
        if (!WatchlistStore.IsPageLink(url)) throw new ArgumentException("The selected source link is unavailable.");
        using var launched = Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private Control LibraryPage()
    {
        FormatRows<LibraryVideo>(library, v => v.Name, v => Joined(v.Category, v.Series, Path.GetExtension(v.FilePath).Trim('.').ToUpperInvariant(), v.HasCaptions ? "Subtitles" : null));
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        AutomationProperties.SetName(filter, "Search library");
        Place(toolbar, filter, 0, 0); Place(toolbar, ActionButton("Refresh", () => _ = RefreshLibraryAsync()), 1, 0);
        filter.TextChanged += (_, _) => { if (selectedPage == 1) _ = RefreshLibraryAsync(true); };
        play = ActionButton("Play video", () => OpenPath(((LibraryVideo)library.SelectedItem).FilePath), true);
        reveal = ActionButton("Open folder", () => OpenPath(Path.GetDirectoryName(((LibraryVideo)library.SelectedItem).FilePath)));
        subtitles = TaskButton("Generate English subtitles", async (token, updates) => {
            var item = (LibraryVideo)library.SelectedItem;
            var result = await CaptionWorker.RunAsync(tools, null, item.FilePath, CaptionMode.English, u => updates.Report(u.Message), token);
            return result.Message;
        });
        narrate = TaskButton("Create English audio", async (token, updates) => {
            var item = (LibraryVideo)library.SelectedItem;
            var voice = (NarrationStyle)libraryStyle.SelectedIndex;
            string result = await EnglishDubWorker.RunAsync(tools, null, item.FilePath, voice, u => updates.Report(u.Message), token);
            return "English audio saved · " + Path.GetFileName(result);
        });
        review = ActionButton("Edit English text", async () => {
            var item = (LibraryVideo)library.SelectedItem;
            try { await PortableTranscriptEditor.ShowAsync(this, item.FilePath, tools); }
            catch (Exception ex) { Report(ex.Message); }
        });
        library.SelectionChanged += (_, _) => RefreshActions();
        library.DoubleTapped += (_, _) => { if (library.SelectedItem is LibraryVideo item) { try { OpenPath(item.FilePath); } catch (Exception ex) { Report(ex.Message); } } };
        libraryStyle.Width = 191;
        var voiceLabel = Text("Audio style", 13, Muted, true);
        voiceLabel.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(libraryStyle, "Library audio style");
        return CollectionPage("Offline library", toolbar, library, libraryEmpty, new StackPanel { Spacing = 3,
            Children = { Buttons(play, reveal, subtitles, review), Buttons(voiceLabel, libraryStyle, narrate) } });
    }

    private async Task RefreshLibraryAsync(bool debounce = false)
    {
        libraryScan?.Cancel();
        using var scan = new CancellationTokenSource();
        libraryScan = scan;
        string directory = folder.Text, search = filter.Text;
        try
        {
            if (debounce) await Task.Delay(220, scan.Token);
            var videos = await Task.Run(() => VideoLibrary.Scan(VideoLibrary.NormalizeRoot(directory), search, scan.Token), scan.Token);
            if (closed || scan.IsCancellationRequested) return;
            string previous = (library.SelectedItem as LibraryVideo)?.FilePath;
            Items(library, libraryEmpty, videos, string.IsNullOrWhiteSpace(search) ?
                "Your saved videos will appear here." : "No videos match this search.");
            if (previous != null) library.SelectedItem = videos.FirstOrDefault(video => video.FilePath == previous);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && !scan.IsCancellationRequested) Items(library, libraryEmpty, Array.Empty<LibraryVideo>(), ex.Message); }
        finally { if (libraryScan == scan) libraryScan = null; if (!closed) RefreshActions(); }
    }

    private Control DiscoverPage()
    {
        FormatRows<CatalogMatch>(results, m => m.Title, m => Joined(m.Year, m.Type, m.Source, m.Summary));
        searchEmpty.Text = "Search for a movie or series to see where it is listed.";
        country.SelectedIndex = Math.Max(0, Array.IndexOf(Countries, preferences.CountryCode));
        AutomationProperties.SetName(query, "Title to search");
        var searchLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Place(searchLine, query, 0, 0);
        find = ActionButton("Search", () => _ = SearchAsync(), true);
        Place(searchLine, find, 1, 0);
        query.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = SearchAsync(); e.Handled = true; } };
        var filters = new StackPanel { Spacing = 8, Children = { searchLine, Pair(Field("Title type", kind), Field("Country", country)) } };
        browse = ActionButton("Open source", () => OpenLink(((CatalogMatch)results.SelectedItem).Url));
        bookmark = ActionButton("Save to watchlist", () => {
            bool added = watchlist.Add((CatalogMatch)results.SelectedItem);
            Report(added ? "Saved to Watchlist." : "Already in Watchlist.");
        });
        addLink = ActionButton("Add YouTube link", () => {
            var match = (CatalogMatch)results.SelectedItem;
            string url = DownloadWorker.NormalizeVideoUrl(match.YouTubeUrl);
            urls.Text = string.IsNullOrWhiteSpace(urls.Text) ? url : urls.Text.TrimEnd() + "\n" + url;
            Navigate(0); urls.Focus();
        });
        results.SelectionChanged += (_, _) => RefreshActions();
        return CollectionPage("Discover", filters, results, searchEmpty, Buttons(browse, bookmark, addLink));
    }

    private async Task SearchAsync()
    {
        discovery?.Cancel();
        using var search = new CancellationTokenSource();
        discovery = search;
        try
        {
            Items(results, searchEmpty, Array.Empty<CatalogMatch>(), "Searching catalogs…");
            var found = await MediaCatalog.SearchAsync(tools, query.Text, (CatalogKind)kind.SelectedIndex,
                Countries[country.SelectedIndex], search.Token);
            if (closed || search.IsCancellationRequested) return;
            Items(results, searchEmpty, found.Matches, "No titles found.");
            if (found.Warnings.Count > 0) Report(string.Join(" · ", found.Warnings));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && !search.IsCancellationRequested) searchEmpty.Text = ex.Message; }
        finally { if (discovery == search) discovery = null; if (!closed) RefreshActions(); }
    }

    private Control WatchlistPage()
    {
        FormatRows<WatchlistEntry>(saved, item => item.Title, item => Joined(item.Year, item.Type, item.Source));
        visit = ActionButton("Open source", () => OpenLink(((WatchlistEntry)saved.SelectedItem).Url), true);
        remove = ActionButton("Remove from watchlist", () => { watchlist.Remove(((WatchlistEntry)saved.SelectedItem).Id); RefreshWatchlist(); });
        saved.SelectionChanged += (_, _) => RefreshActions();
        return CollectionPage("Watchlist", Text("Titles saved from Discover", 14, Muted), saved, savedEmpty, Buttons(visit, remove));
    }
    private void RefreshWatchlist()
    {
        try { Items(saved, savedEmpty, watchlist.Load(), "Save a title from Discover to keep it here."); }
        catch (Exception ex) { Items(saved, savedEmpty, Array.Empty<WatchlistEntry>(), ex.Message); }
        RefreshActions();
    }
    private Control HistoryPage()
    {
        FormatRows<DownloadHistoryEntry>(attempts, e => e.Title,
            e => Joined(e.StartedUtc.ToLocalTime().ToString("g"), e.State == HistoryState.AudioPending ? "English audio pending" : e.State.ToString(), e.Note));
        replay = ActionButton("Play saved video", () => OpenPath(PortableDownloads.SavedFile((DownloadHistoryEntry)attempts.SelectedItem)));
        retry = TaskButton("Retry", (token, updates) =>
            batch.RunAsync(new[] { (DownloadHistoryEntry)attempts.SelectedItem }, 1, updates, token), true);
        attempts.SelectionChanged += (_, _) => RefreshActions();
        return CollectionPage("Download history", ActionButton("Refresh", RefreshHistory), attempts, historyEmpty, Buttons(retry, replay));
    }
    private void RefreshHistory()
    {
        try { Items(attempts, historyEmpty, history.Load(), "Downloads you start will appear here."); }
        catch (Exception ex) { Items(attempts, historyEmpty, Array.Empty<DownloadHistoryEntry>(), ex.Message); }
        RefreshActions();
    }
    private void RefreshActions()
    {
        if (retry == null) return;
        var video = library.SelectedItem as LibraryVideo;
        bool exists = File.Exists(video?.FilePath);
        bool original = exists && !EnglishDubWorker.IsDub(Path.GetFileNameWithoutExtension(video.FilePath));
        bool idle = job == null;
        bool supportsVoice = original && (Path.GetExtension(video.FilePath).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(video.FilePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase));
        play.IsEnabled = reveal.IsEnabled = exists;
        review.IsEnabled = idle && original && File.Exists(Path.ChangeExtension(video.FilePath, ".en.srt"));
        subtitles.IsEnabled = idle && original;
        narrate.IsEnabled = libraryStyle.IsEnabled = idle && supportsVoice;
        var match = results.SelectedItem as CatalogMatch;
        browse.IsEnabled = bookmark.IsEnabled = match != null;
        addLink.IsEnabled = idle && !string.IsNullOrWhiteSpace(match?.YouTubeUrl);
        visit.IsEnabled = remove.IsEnabled = saved.SelectedItem is WatchlistEntry;
        var item = attempts.SelectedItem as DownloadHistoryEntry;
        replay.IsEnabled = PortableDownloads.SavedFile(item) != null;
        retry.IsEnabled = idle && item != null && item.State != HistoryState.Completed && item.State != HistoryState.Running;
        retry.Content = item?.State == HistoryState.AudioPending ? "Retry English audio" : "Retry";
    }
}
