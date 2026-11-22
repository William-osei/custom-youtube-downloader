using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CustomYouTubeDownloader
{
    internal enum HistoryState
    {
        Running,
        Completed,
        Failed,
        Cancelled,
        Interrupted,
        AudioPending
    }

    internal sealed class DownloadHistoryEntry
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public string GroupingUrl { get; set; }
        public string Title { get; set; }
        public string Root { get; set; }
        public string RequestedCategory { get; set; }
        public string RequestedSeries { get; set; }
        public string Destination { get; set; }
        public string FilePath { get; set; }
        public string Note { get; set; }
        public DateTime StartedUtc { get; set; }
        public HistoryState State { get; set; }
        public int MaxHeight { get; set; }
        public bool CompatibleMp4 { get; set; }
        public CaptionMode Captions { get; set; }
        public bool EnglishAudio { get; set; }
        public NarrationStyle AudioStyle { get; set; }
        public int PlaylistPosition { get; set; }
    }

    internal sealed class DownloadHistoryStore
    {
        private const int FinishedLimit = 200;
        private const int JsonLimit = 4 * 1024 * 1024;
        private const int PathLimit = 1024;
        private static readonly object HistoryLock = new object();
        private static readonly Regex NetworkAddress = new Regex(
            @"https?://[^\s<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex GroupingList = new Regex(
            @"\A[A-Za-z0-9_-]{8,108}\z", RegexOptions.CultureInvariant);
        private readonly string path;

        public DownloadHistoryStore(string settingsDirectory)
        {
            path = Path.Combine(settingsDirectory, "history.json");
        }

        public List<DownloadHistoryEntry> Load()
        {
            lock (HistoryLock)
            {
                return Read().OrderByDescending(item => item.StartedUtc).ToList();
            }
        }

        public void Upsert(DownloadHistoryEntry entry)
        {
            var normalized = Normalize(entry);
            lock (HistoryLock)
            {
                var items = Read();
                items.RemoveAll(item => item.Id == normalized.Id);
                items.Insert(0, normalized);
                Write(RetainRecent(items));
            }
        }

        public void RecoverInterrupted()
        {
            lock (HistoryLock)
            {
                var items = Read();
                bool changed = false;
                foreach (var item in items)
                {
                    if (item.State != HistoryState.Running)
                        continue;
                    bool hasOriginal = item.EnglishAudio && item.FilePath != null;
                    item.State = hasOriginal ? HistoryState.AudioPending : HistoryState.Interrupted;
                    item.Note = hasOriginal ? "Original video saved. The app closed before the English audio finished." :
                        "The app closed before this job finished.";
                    changed = true;
                }
                if (changed)
                    Write(RetainRecent(items));
            }
        }

        private static List<DownloadHistoryEntry> RetainRecent(IEnumerable<DownloadHistoryEntry> items)
        {
            int finished = 0;
            var retained = new List<DownloadHistoryEntry>();
            foreach (var item in items.OrderByDescending(item => item.StartedUtc))
            {
                if (item.State == HistoryState.Running)
                    retained.Add(item);
                else if (finished < FinishedLimit)
                {
                    retained.Add(item);
                    finished++;
                }
            }
            return retained;
        }

        private List<DownloadHistoryEntry> Read()
        {
            List<DownloadHistoryEntry> items;
            if (TryRead(path, out items) || TryRead(path + ".bak", out items))
                return items;
            if (File.Exists(path) || File.Exists(path + ".bak"))
                throw new InvalidDataException("The saved download history could not be read.");
            return new List<DownloadHistoryEntry>();
        }

        private static bool TryRead(string filename, out List<DownloadHistoryEntry> items)
        {
            items = null;
            if (!File.Exists(filename))
                return false;
            try
            {
                if (new FileInfo(filename).Length > JsonLimit * 4L)
                    return false;
                var saved = Serializer().Deserialize<List<DownloadHistoryEntry>>(File.ReadAllText(filename, Encoding.UTF8));
                if (saved == null)
                    return false;
                var entries = saved.Select(Normalize).ToList();
                if (entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count)
                    return false;
                items = entries;
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (FormatException) { return false; }
            catch (OverflowException) { return false; }
        }

        private void Write(List<DownloadHistoryEntry> items)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(Serializer().Serialize(items));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path))
                {
                    // When recovering, preserve the good backup instead of overwriting it with the damaged file.
                    List<DownloadHistoryEntry> current;
                    string backup = TryRead(path, out current) ? path + ".bak" : null;
                    File.Replace(temporary, path, backup);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static DownloadHistoryEntry Normalize(DownloadHistoryEntry entry)
        {
            Guid id;
            if (entry == null || !Guid.TryParseExact(entry.Id, "N", out id) || id == Guid.Empty ||
                entry.StartedUtc.Year < 1970 || !Enum.IsDefined(typeof(HistoryState), entry.State) ||
                !Enum.IsDefined(typeof(CaptionMode), entry.Captions) || entry.MaxHeight < 0 ||
                entry.MaxHeight > 16384 || entry.PlaylistPosition < 0 || entry.PlaylistPosition > 5000)
                throw new ArgumentException("The download history entry is invalid.");

            string root = RequiredPath(entry.Root);
            string destination = OptionalPath(entry.Destination);
            string file = OptionalPath(entry.FilePath);
            if ((destination != null && !IsWithin(root, destination)) ||
                (file != null && (!IsWithin(destination ?? root, file) || MatchesPath(destination ?? root, file) ||
                    !IsVideoPath(file))) || (entry.State == HistoryState.Completed && file == null) ||
                (entry.State == HistoryState.AudioPending && (!entry.EnglishAudio || file == null)))
                throw new ArgumentException("The download history contains an invalid video location.");

            string title = ShortText(entry.Title, 220);
            string url = DownloadWorker.NormalizeVideoUrl(ShortText(entry.Url, 500));
            return new DownloadHistoryEntry {
                Id = id.ToString("N"), Url = url, GroupingUrl = NormalizeGroupingUrl(url, entry.GroupingUrl),
                Title = title, Root = root, RequestedCategory = ShortText(entry.RequestedCategory, 150),
                RequestedSeries = ShortText(entry.RequestedSeries, 150),
                Destination = destination, FilePath = file,
                Note = ShortText(NetworkAddress.Replace(entry.Note ?? "", "[link]"), 600),
                StartedUtc = entry.StartedUtc.Kind == DateTimeKind.Unspecified ?
                    DateTime.SpecifyKind(entry.StartedUtc, DateTimeKind.Utc) : entry.StartedUtc.ToUniversalTime(),
                State = entry.State, MaxHeight = entry.MaxHeight, CompatibleMp4 = entry.CompatibleMp4,
                Captions = entry.Captions, EnglishAudio = entry.EnglishAudio,
                AudioStyle = Enum.IsDefined(typeof(NarrationStyle), entry.AudioStyle) ? entry.AudioStyle : NarrationStyle.Faithful,
                PlaylistPosition = entry.PlaylistPosition
            };
        }

        private static string NormalizeGroupingUrl(string url, string grouping)
        {
            string prefix = url + "&list=";
            if (grouping != null && grouping.StartsWith(prefix, StringComparison.Ordinal) &&
                GroupingList.IsMatch(grouping.Substring(prefix.Length)))
                return grouping;
            return url;
        }

        private static bool IsVideoPath(string file)
        {
            string extension = Path.GetExtension(file);
            return String.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(extension, ".mkv", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWithin(string parent, string candidate)
        {
            if (MatchesPath(parent, candidate))
                return true;
            string prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, PlatformTools.PathComparison);
        }

        private static bool MatchesPath(string first, string second)
        {
            return String.Equals(first.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                second.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                PlatformTools.PathComparison);
        }

        private static string RequiredPath(string value)
        {
            string normalized = value != null && value.Length <= PathLimit ?
                AppPreferences.NormalizeFolder(value) : null;
            if (normalized == null)
                throw new ArgumentException("The download history requires an absolute path.");
            return normalized;
        }

        private static string OptionalPath(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : RequiredPath(value);
        }

        private static string ShortText(string value, int limit)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";
            var text = new StringBuilder();
            bool spacePending = false;
            foreach (char character in value)
            {
                if (Char.IsControl(character) || Char.IsWhiteSpace(character))
                {
                    spacePending = text.Length > 0;
                    continue;
                }
                if (spacePending && text.Length < limit)
                    text.Append(' ');
                spacePending = false;
                if (text.Length == limit)
                    break;
                text.Append(character);
            }
            if (text.Length > 0 && Char.IsHighSurrogate(text[text.Length - 1]))
                text.Length--;
            return text.ToString().TrimEnd();
        }

        private static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = JsonLimit, RecursionLimit = 12 };
        }
    }
}
