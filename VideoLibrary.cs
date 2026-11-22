using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CustomYouTubeDownloader
{
    internal sealed class LibraryDestination
    {
        public string DirectoryPath;
        public string Category;
        public string Series;
        public string Warning;
    }

    internal sealed class LibraryVideo
    {
        public string Name;
        public string Category;
        public string Series;
        public string FilePath;
        public DateTime ModifiedAt;
        public bool HasCaptions;
    }

    internal static class VideoLibrary
    {
        private const string Uncategorized = "Uncategorized";
        private const string Standalone = "Standalone";
        private const int MaximumDestinationLength = 155;
        private const int CategoryLimit = 38;
        private const int SeriesLimit = 60;

        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(
            new[] { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".wmv", ".mpg", ".mpeg" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly string[] CaptionExtensions = { ".srt", ".vtt", ".ass", ".ssa" };
        private static readonly Regex CaptionLanguage = new Regex(
            @"\A([a-z]{2,3})(?:-[a-z]{4})?(?:-(?:[a-z]{2}|[0-9]{3}))?(?:-[a-z0-9]{5,8}|-[0-9][a-z0-9]{3})*\z",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Lazy<HashSet<string>> CaptionLanguages = new Lazy<HashSet<string>>(GetCaptionLanguages);
        private static readonly HashSet<char> InvalidPathChars =
            new HashSet<char>(Path.GetInvalidFileNameChars());
        private static readonly Regex ReservedName = new Regex(
            @"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(?:[ .]|\z)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex PlaylistId = new Regex(
            @"\A(?:PL|UU|OLAK5uy_)[A-Za-z0-9_-]{8,100}\z", RegexOptions.CultureInvariant);
        private static readonly Regex EpisodeTitle = new Regex(
            @"\A(.{2,90}?)\s*[\s|:–—(\[\-]\s*(?:(?:season\s+\d+[ .,:/–—-]*)?(?:episode|ep\.?|part|chapter|lesson)\s*#?\s*\d+|s\d+\s*e\d+)(?=[^\p{L}\p{N}]|\z)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex PartialVideo = new Regex(
            @"\[[A-Za-z0-9_-]{11}\] \[(?:\d+|unknown)p\](?:\.f[\w-]+|\.temp)(?=\.)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex DownloadName = new Regex(
            @" \[[A-Za-z0-9_-]{11}\]( \[(?:\d+|unknown)p\])\z", RegexOptions.CultureInvariant);

        public static Task<LibraryDestination> ChooseDestinationAsync(string toolsDirectory,
            string originalUrl, string root, string category, string series,
            Action<DownloadProgress> progress, CancellationToken token)
        {
            return Task.Run(() => ChooseDestination(
                toolsDirectory, originalUrl, root, category, series, progress, token), token);
        }

        private static LibraryDestination ChooseDestination(string toolsDirectory, string originalUrl,
            string root, string category, string series, Action<DownloadProgress> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var url = DownloadWorker.NormalizeVideoUrl(originalUrl);
            string folder = NormalizeRoot(root);
            string warning = null;
            bool autoCategory = String.IsNullOrWhiteSpace(category);
            bool autoSeries = String.IsNullOrWhiteSpace(series);

            if (folder.Length > MaximumDestinationLength)
                throw new ArgumentException("Choose a library folder with a shorter path.");

            if (autoCategory || autoSeries)
            {
                if (progress != null)
                    progress(new DownloadProgress { Message = "Checking the video’s category and series…" });

                var metadata = TryReadMetadata(toolsDirectory, url, false, token);
                if (metadata == null)
                    warning = "YouTube’s grouping details were unavailable. Default folder names were used where needed.";
                if (autoCategory)
                    category = FirstCategory(metadata);
                if (autoSeries)
                    series = ChooseSeries(toolsDirectory, originalUrl, metadata, token);
            }

            int separatorCount = folder.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? 1 : 2;
            int available = MaximumDestinationLength - folder.Length - separatorCount;
            if (available < 12)
                return new LibraryDestination {
                    DirectoryPath = folder,
                    Category = Uncategorized,
                    Series = Standalone,
                    Warning = "The library path is too long for category and series folders. Using the library root."
                };

            string safeCategory = SafeFolderName(category, Uncategorized, Math.Min(CategoryLimit, available / 2));
            string safeSeries = SafeFolderName(series, Standalone, Math.Min(SeriesLimit, available - safeCategory.Length));
            string categoryFolder = Path.Combine(folder, safeCategory);
            string destination = Path.Combine(categoryFolder, safeSeries);
            VerifyDestinationSegment(categoryFolder);
            VerifyDestinationSegment(destination);

            return new LibraryDestination {
                DirectoryPath = destination,
                Category = safeCategory,
                Series = safeSeries,
                Warning = warning
            };
        }

        private static string ChooseSeries(string toolsDirectory, string originalUrl,
            IDictionary<string, object> video, CancellationToken token)
        {
            string series = MetadataText(video, "series");
            if (series != null)
                return series;

            string playlist = GetPlaylistId(originalUrl);
            if (playlist != null)
            {
                var playlistData = TryReadMetadata(toolsDirectory, "https://www.youtube.com/playlist?list=" + playlist, true, token);
                string title = MetadataText(playlistData, "title");
                if (title != null)
                    return title;
            }

            string videoTitle = MetadataText(video, "title");
            if (videoTitle != null)
            {
                Match episode = EpisodeTitle.Match(videoTitle);
                if (episode.Success)
                    return episode.Groups[1].Value.Trim(' ', '-', ':', '|', '–', '—');
            }

            return MetadataText(video, "channel") ?? MetadataText(video, "uploader") ?? Standalone;
        }

        internal static string GetPlaylistId(string originalUrl)
        {
            string text = originalUrl.Trim();
            if (text.IndexOf("://", StringComparison.Ordinal) < 0)
                text = "https://" + text;
            Uri url;
            if (!Uri.TryCreate(text, UriKind.Absolute, out url))
                return null;
            string found = null;
            foreach (string value in url.Query.TrimStart('?').Split('&'))
            {
                if (!value.StartsWith("list=", StringComparison.Ordinal))
                    continue;
                if (found != null)
                    return null;
                found = Uri.UnescapeDataString(value.Substring(5));
            }

            return found != null && PlaylistId.IsMatch(found) ? found : null;
        }

        private static IDictionary<string, object> TryReadMetadata(string toolsDirectory, string url,
            bool playlist, CancellationToken token)
        {
            string toolPath = Path.GetFullPath(toolsDirectory);
            string engine = Path.Combine(toolPath, "yt-dlp.exe");
            if (!File.Exists(engine))
                return null;

            var arguments = new List<string> {
                "--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--skip-download",
                "--socket-timeout", "7", "--retries", "0", "--extractor-retries", "0",
                "--ignore-no-formats-error", "--encoding", "utf-8", "--no-js-runtimes"
            };
            if (playlist)
                arguments.AddRange(new[] {
                    "--yes-playlist", "--flat-playlist", "--playlist-items", "1", "--print", "playlist:%(title)j"
                });
            else
                arguments.AddRange(new[] {
                    "--no-playlist", "--extractor-args",
                    "youtube:player_client=web;player_skip=configs,js;skip=hls,dash,translated_subs",
                    "--print", "%(.{title,series,channel,uploader,categories})j"
                });
            arguments.AddRange(new[] { "--", url });

            using (var process = new Process { StartInfo = new ProcessStartInfo {
                FileName = engine,
                WorkingDirectory = toolPath,
                Arguments = DownloadWorker.JoinArguments(arguments),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                RedirectStandardError = true
            } })
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!process.Start())
                        return null;
                }
                catch (Win32Exception)
                {
                    return null;
                }

                try
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var error = process.StandardError.ReadToEndAsync();
                    var clock = Stopwatch.StartNew();
                    while (!process.WaitForExit(100) || !output.IsCompleted || !error.IsCompleted)
                    {
                        token.ThrowIfCancellationRequested();
                        if (clock.ElapsedMilliseconds >= 15000)
                            return null;
                    }
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        return null;

                    var serializer = new JavaScriptSerializer();
                    string json = output.GetAwaiter().GetResult().Trim();
                    if (playlist)
                    {
                        return new Dictionary<string, object> {
                            { "title", serializer.Deserialize<string>(json) }
                        };
                    }
                    return serializer.Deserialize<Dictionary<string, object>>(json);
                }
                catch (ArgumentException)
                {
                    return null;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
                catch (IOException)
                {
                    return null;
                }
                finally
                {
                    if (!process.HasExited)
                        DownloadWorker.StopProcessTree(process);
                }
            }
        }

        private static string MetadataText(IDictionary<string, object> metadata, string field)
        {
            object value;
            string label = metadata != null && metadata.TryGetValue(field, out value) ? value as string : null;
            return String.IsNullOrWhiteSpace(label) || label == "NA" ? null : label;
        }

        private static string FirstCategory(IDictionary<string, object> metadata)
        {
            object value;
            if (metadata == null || !metadata.TryGetValue("categories", out value) || value is string)
                return Uncategorized;
            var array = value as IEnumerable;
            if (array == null)
                return Uncategorized;
            foreach (object category in array)
            {
                string text = category as string;
                if (!String.IsNullOrWhiteSpace(text))
                    return text;
            }
            return Uncategorized;
        }

        internal static string NormalizeRoot(string root)
        {
            string path = (root ?? "").Trim();
            if (!Path.IsPathRooted(path) ||
                (!path.StartsWith(@"\\", StringComparison.Ordinal) &&
                    (path.Length < 3 || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))) ||
                path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                path.StartsWith(@"\\?\", StringComparison.Ordinal))
                throw new ArgumentException("Choose a full path for the video library folder.");

            string fullPath = Path.GetFullPath(path);
            string volume = Path.GetPathRoot(fullPath);
            return fullPath == volume ? fullPath : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal static string ValidateRetryDestination(string root, string destination)
        {
            string basePath = NormalizeRoot(root);
            string target = NormalizeRoot(destination);
            if (target.Length > MaximumDestinationLength)
                throw new ArgumentException("The saved download folder path is too long.");
            if (String.Equals(basePath, target, StringComparison.OrdinalIgnoreCase))
                return target;

            string prefix = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The saved destination is outside its download folder.");
            string current = basePath;
            foreach (string segment in target.Substring(prefix.Length).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                VerifyDestinationSegment(current);
            }
            return target;
        }

        private static string SafeFolderName(string label, string fallback, int limit)
        {
            var name = new StringBuilder();
            foreach (char ch in label ?? "")
                name.Append(InvalidPathChars.Contains(ch) || Char.IsControl(ch) ? ' ' : ch);

            string result = Regex.Replace(name.ToString(), @"\s+", " ").Trim(' ', '.');
            if (result.Length == 0)
                result = fallback;
            if (ReservedName.IsMatch(result))
                result = "_" + result;
            if (result.Length > limit)
            {
                int length = Char.IsHighSurrogate(result[limit - 1]) ? limit - 1 : limit;
                result = result.Substring(0, length).TrimEnd(' ', '.');
            }
            return result;
        }

        private static void VerifyDestinationSegment(string path)
        {
            if (File.Exists(path))
                throw new IOException("A file uses the name of a library folder. Pick a different category or series.");
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A library folder points elsewhere. Pick a different category or series.");
        }

        public static List<LibraryVideo> Scan(string root, string filter, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = new List<LibraryVideo>();
            if (String.IsNullOrWhiteSpace(root))
                return result;
            var pending = new Stack<LibraryFolder>();
            pending.Push(new LibraryFolder {
                Path = NormalizeRoot(root), Depth = 0, Category = Uncategorized, Series = Standalone
            });
            var words = Regex.Split((filter ?? "").Trim(), @"\s+").Where(word => word.Length > 0).ToArray();

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var folder = pending.Pop();
                var entries = ReadFolderEntries(folder.Path);
                var subtitles = ReadCaptionIndex(entries, token);
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;
                        var child = entry as DirectoryInfo;
                        if (child != null)
                        {
                            pending.Push(new LibraryFolder {
                                Path = child.FullName,
                                Depth = folder.Depth + 1,
                                Category = folder.Depth == 0 ? child.Name : folder.Category,
                                Series = folder.Depth == 1 ? child.Name : folder.Series
                            });
                            continue;
                        }

                        LibraryVideo video = ReadVideo(entry as FileInfo, folder, subtitles, words);
                        if (video != null)
                            result.Add(video);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            return result.OrderByDescending(video => video.ModifiedAt)
                .ThenBy(video => video.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static FileSystemInfo[] ReadFolderEntries(string path)
        {
            try
            {
                return new DirectoryInfo(path).GetFileSystemInfos();
            }
            catch (IOException) { return new FileSystemInfo[0]; }
            catch (UnauthorizedAccessException) { return new FileSystemInfo[0]; }
        }

        public static string FindPreferredCaption(string videoPath)
        {
            if (String.IsNullOrWhiteSpace(videoPath))
                return null;
            string path = Path.GetFullPath(videoPath);
            var entries = ReadFolderEntries(Path.GetDirectoryName(path));
            var captions = ReadCaptionIndex(entries, CancellationToken.None);
            CaptionFile found;
            return captions.TryGetValue(Path.GetFileNameWithoutExtension(path), out found) ? found.Path : null;
        }

        private static Dictionary<string, CaptionFile> ReadCaptionIndex(FileSystemInfo[] entries, CancellationToken token)
        {
            var videoStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in entries.OfType<FileInfo>())
            {
                token.ThrowIfCancellationRequested();
                if (VideoExtensions.Contains(file.Extension) && !PartialVideo.IsMatch(file.Name))
                    videoStems.Add(Path.GetFileNameWithoutExtension(file.Name));
            }

            var captions = new Dictionary<string, CaptionFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in entries.OfType<FileInfo>())
            {
                token.ThrowIfCancellationRequested();
                int format = Array.FindIndex(CaptionExtensions, ext => ext.Equals(file.Extension, StringComparison.OrdinalIgnoreCase));
                if (format < 0 || !IsUsableCaption(file))
                    continue;

                string stem = Path.GetFileNameWithoutExtension(file.Name);
                int languageRank = 1;
                // Exact ownership always wins. Film.en.srt belongs to Film.en.mkv when both Film.en.mkv and Film.mkv exist.
                if (!videoStems.Contains(stem))
                {
                    int lastDot = stem.LastIndexOf('.');
                    if (lastDot <= 0)
                        continue;
                    var tag = CaptionLanguage.Match(stem.Substring(lastDot + 1));
                    if (!tag.Success || !CaptionLanguages.Value.Contains(tag.Groups[1].Value))
                        continue;
                    string language = tag.Groups[1].Value;
                    languageRank = language.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                        language.Equals("eng", StringComparison.OrdinalIgnoreCase) ? 0 : 2;
                    stem = stem.Substring(0, lastDot);
                    if (!videoStems.Contains(stem))
                        continue;
                }

                int rank = languageRank * CaptionExtensions.Length + format;
                CaptionFile current;
                if (!captions.TryGetValue(stem, out current) || rank < current.Rank ||
                    (rank == current.Rank && StringComparer.OrdinalIgnoreCase.Compare(file.FullName, current.Path) < 0))
                    captions[stem] = new CaptionFile { Path = file.FullName, Rank = rank };
            }
            return captions;
        }

        private static bool IsUsableCaption(FileInfo file)
        {
            try { return (file.Attributes & FileAttributes.ReparsePoint) == 0 && file.Length > 0; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static HashSet<string> GetCaptionLanguages()
        {
            // Include common ISO 639-2 bibliographic aliases as well as the language codes known to Windows.
            var codes = new HashSet<string>(
                "alb arm baq bur chi cze dut fre geo ger gre ice mac mao may per rum slo tib wel".Split(' '),
                StringComparer.OrdinalIgnoreCase);
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
            {
                codes.Add(culture.TwoLetterISOLanguageName);
                codes.Add(culture.ThreeLetterISOLanguageName);
            }
            return codes;
        }

        private static LibraryVideo ReadVideo(FileInfo file, LibraryFolder folder,
            Dictionary<string, CaptionFile> subtitles, string[] words)
        {
            if (file == null || !VideoExtensions.Contains(file.Extension) ||
                PartialVideo.IsMatch(file.Name) || file.Length == 0)
                return null;
            string stem = Path.GetFileNameWithoutExtension(file.Name);
            string sourceStem = EnglishDubWorker.SourceStem(stem);
            string title = DownloadName.Replace(sourceStem, "$1") + stem.Substring(sourceStem.Length);
            string searchable = title + "\n" + folder.Category + "\n" + folder.Series;
            if (!words.All(word => searchable.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                return null;

            return new LibraryVideo {
                Name = title,
                Category = folder.Category,
                Series = folder.Series,
                FilePath = file.FullName,
                ModifiedAt = file.LastWriteTime,
                HasCaptions = subtitles.ContainsKey(stem) || sourceStem.Length != stem.Length
            };
        }

        private sealed class CaptionFile
        {
            public string Path;
            public int Rank;
        }

        private sealed class LibraryFolder
        {
            public string Path;
            public string Category;
            public string Series;
            public int Depth;
        }
    }
}
