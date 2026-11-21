using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CustomYouTubeDownloader
{
    internal sealed class PlaylistResult
    {
        public string Title;
        public string Id;
        public List<PlaylistEntry> Entries;
        public int UnavailableCount;
    }

    internal sealed class PlaylistEntry
    {
        public string VideoUrl;
        public string Title;
        public int Position;
    }

    internal static class PlaylistWorker
    {
        private const int MaxEntries = 5000;
        private const int MaxJsonLength = 32 * 1024 * 1024;
        private const string PlaylistBaseUrl = "https://www.youtube.com/playlist?list=";
        private const string WatchBaseUrl = "https://www.youtube.com/watch?v=";
        private const string InvalidLinkMessage = "Paste a YouTube playlist link from the playlist page.";

        private static readonly Regex PlaylistIdPattern = new Regex(
            @"\A(?:PL|UU|UL|FL|OLAK5uy_)[A-Za-z0-9_-]{8,100}\z", RegexOptions.CultureInvariant);
        private static readonly Regex VideoIdPattern = new Regex(
            @"\A[A-Za-z0-9_-]{11}\z", RegexOptions.CultureInvariant);

        public static bool TryNormalizePlaylistUrl(string input, out string canonicalUrl)
        {
            canonicalUrl = null;
            string text = (input ?? "").Trim();
            if (text.IndexOf("://", StringComparison.Ordinal) < 0)
                text = "https://" + text;

            Uri url;
            if (!Uri.TryCreate(text, UriKind.Absolute, out url) ||
                (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) ||
                !url.IsDefaultPort || !string.IsNullOrEmpty(url.UserInfo) ||
                !IsPlaylistHost(url.Host) ||
                !(url.AbsolutePath == "/playlist" || url.AbsolutePath == "/playlist/"))
                return false;

            string id = null;
            foreach (string parameter in url.Query.TrimStart('?').Split('&'))
            {
                if (!parameter.StartsWith("list=", StringComparison.Ordinal))
                    continue;
                if (id != null)
                    return false;
                id = Uri.UnescapeDataString(parameter.Substring(5));
            }
            if (id == null || !PlaylistIdPattern.IsMatch(id))
                return false;

            canonicalUrl = PlaylistBaseUrl + id;
            return true;
        }

        private static bool IsPlaylistHost(string host)
        {
            switch (host.ToLowerInvariant())
            {
                case "youtube.com":
                case "www.youtube.com":
                case "m.youtube.com":
                case "music.youtube.com":
                case "kids.youtube.com":
                    return true;
                default:
                    return false;
            }
        }

        public static Task<PlaylistResult> ReadAsync(string toolsDirectory, string url, CancellationToken token)
        {
            return Task.Run(() =>
            {
                string canonicalUrl;
                if (!TryNormalizePlaylistUrl(url, out canonicalUrl))
                    throw new ArgumentException(InvalidLinkMessage);
                var data = ReadMetadata(Path.GetFullPath(toolsDirectory), canonicalUrl, token);
                return ParseResult(data, canonicalUrl.Substring(PlaylistBaseUrl.Length), token);
            }, token);
        }

        private static string ReadMetadata(string toolsDirectory, string url, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string executable = Path.Combine(toolsDirectory, "yt-dlp.exe");
            if (!File.Exists(executable))
                throw new InvalidOperationException("Download tools are missing. Run setup-engines.ps1, then retry.");

            var args = new List<string> {
                "--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--color", "never",
                "--socket-timeout", "15", "--retries", "1", "--extractor-retries", "0",
                "--encoding", "utf-8", "--skip-download", "--yes-playlist", "--flat-playlist",
                "--playlist-end", (MaxEntries + 1).ToString(CultureInfo.InvariantCulture),
                "--dump-single-json"
            };
            string node = DownloadWorker.FindNode(toolsDirectory);
            if (node != null)
                args.AddRange(new[] { "--no-js-runtimes", "--js-runtimes", "node:" + node });
            args.AddRange(new[] { "--", url });

            using (var process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = executable,
                    Arguments = DownloadWorker.JoinArguments(args),
                    WorkingDirectory = toolsDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            })
            {
                token.ThrowIfCancellationRequested();
                if (!process.Start())
                    throw new InvalidOperationException("Couldn’t start the playlist reader.");
                try
                {
                    var output = ReadBoundedAsync(process.StandardOutput);
                    process.BeginErrorReadLine();
                    var clock = Stopwatch.StartNew();
                    while (!process.WaitForExit(100) || !output.IsCompleted)
                    {
                        token.ThrowIfCancellationRequested();
                        if (output.IsFaulted)
                            return output.GetAwaiter().GetResult();
                        if (clock.Elapsed > TimeSpan.FromSeconds(90))
                            throw new InvalidOperationException("YouTube took too long to list this playlist. Try again.");
                    }
                    token.ThrowIfCancellationRequested();
                    string data = output.GetAwaiter().GetResult();
                    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(data))
                        throw new InvalidOperationException("Couldn’t read this playlist. Check that it’s available without signing in.");
                    return data;
                }
                finally
                {
                    if (!process.HasExited)
                        DownloadWorker.StopProcessTree(process);
                }
            }
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader)
        {
            var output = new StringBuilder();
            var buffer = new char[8192];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > MaxJsonLength)
                    throw new InvalidOperationException("This playlist is too large to load at once.");
                output.Append(buffer, 0, count);
            }
            return output.ToString();
        }

        private static PlaylistResult ParseResult(string json, string id, CancellationToken token)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = MaxJsonLength, RecursionLimit = 60 };
            Dictionary<string, object> playlist;
            try
            {
                playlist = serializer.Deserialize<Dictionary<string, object>>(json);
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("YouTube returned a playlist the app couldn’t read.");
            }

            object rawEntries;
            var entries = playlist != null && playlist.TryGetValue("entries", out rawEntries)
                ? rawEntries as IList : null;
            if (entries == null)
                throw new InvalidOperationException("YouTube didn’t return a list of videos for this playlist.");

            int expectedCount = NumberField(playlist, "playlist_count");
            if (entries.Count > MaxEntries || expectedCount > MaxEntries)
                throw new InvalidOperationException("This playlist has more than 5,000 videos. Paste individual video links to download a selection.");

            string title = TextField(playlist, "title");
            var result = new PlaylistResult {
                Title = string.IsNullOrWhiteSpace(title) ? "YouTube Playlist" : title,
                Id = id,
                Entries = new List<PlaylistEntry>()
            };
            int lastPosition = 0;
            foreach (object raw in entries)
            {
                token.ThrowIfCancellationRequested();
                var entry = raw as Dictionary<string, object>;
                int index = NumberField(entry, "playlist_index");
                int position = index > lastPosition && index <= MaxEntries ? index : lastPosition + 1;
                lastPosition = position;
                string videoId = TextField(entry, "id");
                string name = TextField(entry, "title");
                if (videoId == null || !VideoIdPattern.IsMatch(videoId) || IsUnavailable(entry, name))
                    continue;

                result.Entries.Add(new PlaylistEntry {
                    VideoUrl = WatchBaseUrl + videoId,
                    Title = string.IsNullOrWhiteSpace(name) ? "Video " + position : name,
                    Position = position
                });
            }
            result.UnavailableCount = Math.Max(expectedCount, lastPosition) - result.Entries.Count;
            return result;
        }

        private static bool IsUnavailable(Dictionary<string, object> entry, string name)
        {
            if (name != null)
            {
                string plainName = name.Trim('[', ']', ' ');
                if (plainName.Equals("Private video", StringComparison.OrdinalIgnoreCase) ||
                    plainName.Equals("Deleted video", StringComparison.OrdinalIgnoreCase) ||
                    plainName.Equals("Unavailable video", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            string availability = TextField(entry, "availability");
            if (availability == "private" || availability == "premium_only" ||
                availability == "subscriber_only" || availability == "needs_auth")
                return true;

            string liveStatus = TextField(entry, "live_status");
            return liveStatus == "is_live" || liveStatus == "is_upcoming";
        }

        private static int NumberField(Dictionary<string, object> data, string key)
        {
            object value;
            int number;
            return data != null && data.TryGetValue(key, out value) &&
                Int32.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.None, CultureInfo.InvariantCulture, out number)
                ? number : 0;
        }

        private static string TextField(Dictionary<string, object> data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) ? value as string : null;
        }
    }
}
