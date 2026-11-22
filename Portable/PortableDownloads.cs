using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CustomYouTubeDownloader;

internal sealed class PortableDownloads
{
    private readonly string tools;
    private readonly DownloadHistoryStore history;

    internal PortableDownloads(string toolsDirectory, DownloadHistoryStore store)
    {
        tools = toolsDirectory;
        history = store;
    }

    internal static string SavedFile(DownloadHistoryEntry item)
    {
        if (item == null || (item.State != HistoryState.Completed && item.State != HistoryState.AudioPending) ||
            !File.Exists(item.FilePath) || string.IsNullOrWhiteSpace(item.Destination)) return null;
        try
        {
            string target = VideoLibrary.ValidateRetryDestination(item.Root, item.Destination);
            string file = Path.GetFullPath(item.FilePath);
            return string.Equals(Path.GetDirectoryName(file), target, PlatformTools.PathComparison) &&
                string.Equals(Path.GetExtension(file), item.CompatibleMp4 ? ".mp4" : ".mkv", StringComparison.OrdinalIgnoreCase) &&
                (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 ? file : null;
        }
        catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
        { return null; }
    }

    internal async Task<string> DownloadAsync(string input, AppPreferences settings, string category, string series,
        IProgress<string> progress, CancellationToken token)
    {
        var entries = new List<DownloadHistoryEntry>();
        var links = new List<(string Url, bool IsPlaylist)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var notices = new List<string>();
        string root = VideoLibrary.NormalizeRoot(settings.Folder);
        foreach (string line in input.Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0))
        {
            bool playlist = PlaylistWorker.TryNormalizePlaylistUrl(line, out string normalized);
            string url = playlist ? normalized : DownloadWorker.NormalizeVideoUrl(line);
            if (seen.Add(url)) links.Add((playlist ? url : line, playlist));
        }
        if (links.Count == 0) throw new ArgumentException("Paste at least one video or playlist link.");
        DownloadHistoryEntry NewEntry(string inputUrl, string title, string destination, int position)
        {
            string url = DownloadWorker.NormalizeVideoUrl(inputUrl);
            string playlistId = VideoLibrary.GetPlaylistId(inputUrl);
            return new DownloadHistoryEntry {
                Id = Guid.NewGuid().ToString("N"), Url = url, GroupingUrl = playlistId == null ? url : url + "&list=" + playlistId,
                Title = title ?? url, Root = root, RequestedCategory = category, RequestedSeries = series,
                Destination = destination, PlaylistPosition = position, StartedUtc = DateTime.UtcNow,
                MaxHeight = new[] { 0, 2160, 1440, 1080, 720 }[settings.Quality], CompatibleMp4 = settings.Format == 1,
                Captions = (CaptionMode)settings.Captions, EnglishAudio = settings.EnglishAudio, AudioStyle = settings.AudioStyle
            };
        }
        foreach (var link in links)
        {
            token.ThrowIfCancellationRequested();
            if (!link.IsPlaylist) { entries.Add(NewEntry(link.Url, null, null, 0)); continue; }
            try
            {
                progress.Report("Reading playlist…");
                var playlist = await PlaylistWorker.ReadAsync(tools, link.Url, token);
                if (playlist.UnavailableCount > 0) notices.Add(playlist.UnavailableCount + " unavailable in " + playlist.Title);
                if (playlist.Entries.Count == 0) continue;
                var destination = await VideoLibrary.ChooseDestinationAsync(tools, playlist.Entries[0].VideoUrl,
                    root, category, string.IsNullOrWhiteSpace(series) ? playlist.Title : series, null, token);
                foreach (var child in playlist.Entries)
                    entries.Add(NewEntry(child.VideoUrl, child.Title, destination.DirectoryPath, child.Position));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { notices.Add("Playlist " + link.Url + " · " + ex.Message); }
        }
        string summary = entries.Count == 0 ? "No available videos." :
            await RunAsync(entries, settings.ParallelDownloads, progress, token);
        return notices.Count == 0 ? summary : summary + " " + string.Join(" · ", notices);
    }

    internal async Task<string> RunAsync(IList<DownloadHistoryEntry> entries, int limit, IProgress<string> progress,
        CancellationToken token)
    {
        Action<DownloadProgress> Updates(DownloadHistoryEntry item)
        {
            string title = item.Title == item.Url ? item.Url.Substring(item.Url.Length - 11) : item.Title;
            return update => progress.Report(title + " · " + update.Message);
        }
        void Interrupted(DownloadHistoryEntry item)
        {
            bool saved = !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath);
            item.State = !saved ? HistoryState.Cancelled : item.EnglishAudio ? HistoryState.AudioPending : HistoryState.Completed;
            item.Note = saved ? "Original video saved. Processing was interrupted." : "Download cancelled.";
        }
        async Task<bool> Transfer(int index)
        {
            var item = entries[index];
            bool reuse = item.State == HistoryState.AudioPending && SavedFile(item) != null;
            item.State = HistoryState.Running;
            item.StartedUtc = DateTime.UtcNow;
            if (!reuse) item.FilePath = null;
            item.Note = reuse ? "Resuming English audio." : "Preparing download.";
            history.Upsert(item);
            try
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(item.Destination))
                {
                    var group = await VideoLibrary.ChooseDestinationAsync(tools, item.GroupingUrl ?? item.Url, item.Root,
                        item.RequestedCategory, item.RequestedSeries, Updates(item), token);
                    item.Destination = group.DirectoryPath;
                }
                string directory = VideoLibrary.ValidateRetryDestination(item.Root, item.Destination);
                if (!reuse)
                {
                    var result = await DownloadWorker.RunAsync(tools, item.Url, directory, item.MaxHeight,
                        item.CompatibleMp4, Updates(item), token, item.PlaylistPosition);
                    item.FilePath = result.FilePath;
                }
                if (item.Title == item.Url) item.Title = Path.GetFileNameWithoutExtension(item.FilePath);
                item.State = item.EnglishAudio ? HistoryState.Running : HistoryState.Completed;
                item.Note = item.EnglishAudio ? "Original saved. English audio pending." : "Video saved.";
                return item.EnglishAudio || item.Captions != CaptionMode.None;
            }
            catch (OperationCanceledException) { Interrupted(item); throw; }
            catch (Exception ex) { item.State = HistoryState.Failed; item.Note = ex.Message; return false; }
            finally { history.Upsert(item); }
        }
        async Task Finish(int index)
        {
            var item = entries[index];
            try
            {
                token.ThrowIfCancellationRequested();
                string subtitleNote = null;
                if (item.Captions != CaptionMode.None)
                {
                    try
                    {
                        var captions = await CaptionWorker.RunAsync(tools, item.Url, item.FilePath, item.Captions, Updates(item), token);
                        if (captions.MissingRequested) subtitleNote = captions.Message;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { subtitleNote = ex.Message; }
                }
                if (item.EnglishAudio)
                    item.FilePath = await EnglishDubWorker.RunAsync(tools, item.Url, item.FilePath, item.AudioStyle, Updates(item), token);
                item.State = HistoryState.Completed;
                item.Note = subtitleNote == null ? "Ready to watch." : "Video saved. " + subtitleNote;
            }
            catch (OperationCanceledException) { Interrupted(item); throw; }
            catch (Exception ex) { item.State = HistoryState.AudioPending; item.Note = "Original saved. " + ex.Message; }
            finally { history.Upsert(item); }
        }
        await DownloadBatchRunner.RunAsync(entries.Select(e => e.Url).ToList(), limit, Transfer, Finish, token);
        int finished = entries.Count(e => e.State == HistoryState.Completed || e.State == HistoryState.AudioPending);
        var notes = new List<string> { finished == 1 ? "1 video saved." : finished + " videos saved." };
        var issue = entries.FirstOrDefault(e => e.State != HistoryState.Completed || e.Note.StartsWith("Video saved. ", StringComparison.Ordinal));
        if (issue != null) notes.Add(issue.Note + " See History for details.");
        return string.Join(" ", notes);
    }
}
