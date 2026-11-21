using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CustomYouTubeDownloader
{
    internal sealed class DownloadProgress
    {
        public string Message;
        public double? Percent;
    }

    internal sealed class DownloadResult
    {
        public string FilePath;
    }

    internal static class DownloadWorker
    {
        private const string FileMarker = "CYD_FILE:";
        private const string ProgressMarker = "CYD_PROGRESS:";
        private const string ProcessingMarker = "CYD_PROCESSING:";
        private static readonly Regex VideoIdPattern =
            new Regex(@"\A[A-Za-z0-9_-]{11}\z", RegexOptions.CultureInvariant);

        public static string NormalizeVideoUrl(string text)
        {
            const string error = "Paste a link to one YouTube video, Short, or replay.";
            text = (text ?? "").Trim();
            if (text.IndexOf("://", StringComparison.Ordinal) < 0)
                text = "https://" + text;

            Uri url;
            if (!Uri.TryCreate(text, UriKind.Absolute, out url) ||
                (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) ||
                !url.IsDefaultPort || !string.IsNullOrEmpty(url.UserInfo))
            {
                throw new ArgumentException(error);
            }

            string id = null;
            string[] segments = url.AbsolutePath.Trim('/').Split('/');
            switch (url.Host.ToLowerInvariant())
            {
                case "youtu.be":
                case "www.youtu.be":
                    if (segments.Length == 1)
                        id = segments[0];
                    break;
                case "youtube.com":
                case "www.youtube.com":
                case "m.youtube.com":
                case "music.youtube.com":
                    if (segments.Length == 1 && segments[0] == "watch")
                        id = FindVideoId(url.Query);
                    else if (segments.Length == 2 &&
                        (segments[0] == "shorts" || segments[0] == "live" || segments[0] == "embed"))
                        id = segments[1];
                    break;
            }

            if (id == null || !VideoIdPattern.IsMatch(id))
                throw new ArgumentException(error);

            return "https://www.youtube.com/watch?v=" + id;
        }

        public static Task<DownloadResult> RunAsync(string toolsDirectory, string url, string directory,
            int maxHeight, bool compatibleMp4, Action<DownloadProgress> progress, CancellationToken cancellation,
            int playlistPosition = 0)
        {
            return Task.Run(() =>
            {
                string videoUrl = NormalizeVideoUrl(url);
                return Run(toolsDirectory, videoUrl, directory, maxHeight, compatibleMp4, progress, cancellation, playlistPosition);
            }, cancellation);
        }

        private static string FindVideoId(string query)
        {
            string found = null;
            foreach (var parameter in query.TrimStart('?').Split('&'))
            {
                int equal = parameter.IndexOf('=');
                if (equal < 0 || parameter.Substring(0, equal) != "v")
                    continue;
                if (found != null)
                    return null;
                found = Uri.UnescapeDataString(parameter.Substring(equal + 1));
            }
            return found;
        }

        private static DownloadResult Run(string toolsDirectory, string url, string directory, int maxHeight,
            bool compatibleMp4, Action<DownloadProgress> progress, CancellationToken cancellation, int playlistPosition)
        {
            cancellation.ThrowIfCancellationRequested();
            if (maxHeight < 0)
                throw new ArgumentOutOfRangeException("maxHeight");

            string toolPath = Path.GetFullPath(toolsDirectory);
            string downloader = Path.Combine(toolPath, "yt-dlp.exe");
            if (!File.Exists(downloader) || !File.Exists(Path.Combine(toolPath, "ffmpeg.exe")) ||
                !File.Exists(Path.Combine(toolPath, "ffprobe.exe")))
            {
                throw new InvalidOperationException("Download tools are missing. Run setup-engines.ps1 from the project folder, then retry.");
            }

            string node = FindNode(toolPath);
            if (node == null)
                throw new InvalidOperationException("Node.js is required for YouTube format detection. Install a supported Node.js version and retry.");

            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
                throw new ArgumentException("Choose a folder with an absolute path for your downloads.");

            string output = Directory.CreateDirectory(Path.GetFullPath(directory)).FullName;
            int titleLimit = Math.Min(100, 215 - output.Length - 40);
            if (titleLimit < 20)
                throw new ArgumentException("Choose a download folder with a shorter path.");

            string filePrefix = playlistPosition > 0 ? playlistPosition.ToString("D3", CultureInfo.InvariantCulture) + " - " : "";
            var arguments = CreateArguments(url, output, toolPath, node, maxHeight, compatibleMp4, titleLimit, filePrefix);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = downloader,
                    Arguments = JoinArguments(arguments),
                    WorkingDirectory = toolPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            var outputLines = new ConcurrentQueue<string>();
            var errors = new Queue<string>();
            string filepath = null;
            int closedStreams = 0;
            process.OutputDataReceived += (sender, args) =>
            {
                if (args.Data == null)
                    Interlocked.Increment(ref closedStreams);
                else
                    outputLines.Enqueue(args.Data);
            };
            process.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data == null)
                    Interlocked.Increment(ref closedStreams);
                else
                    outputLines.Enqueue(args.Data);
            };

            using (process)
            {
                bool started = false;
                try
                {
                    Report(progress, "Checking available video quality\u2026", null);
                    started = process.Start();
                    if (!started)
                        throw new InvalidOperationException("Couldn't start the video downloader.");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    bool outputComplete = WaitForToolOutput(process, () => Volatile.Read(ref closedStreams) == 2,
                        () => ReadOutput(outputLines, errors, progress, ref filepath), cancellation);
                    if (process.ExitCode != 0)
                    {
                        string detail = errors.Count > 0 ? string.Join(Environment.NewLine, errors.ToArray()) :
                            "The downloader exited with code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ".";
                        throw new InvalidOperationException(detail);
                    }
                    if (!outputComplete)
                        throw new InvalidOperationException("The downloader stopped before closing its output. Try the download again.");
                }
                finally
                {
                    if (started)
                        StopProcessTree(process);
                }
            }

            if (string.IsNullOrEmpty(filepath) || !Path.IsPathRooted(filepath))
                throw new InvalidOperationException("No completed video was reported. The link may be unavailable or still live.");

            string resultPath = Path.GetFullPath(filepath);
            string resultDirectory = Path.GetDirectoryName(resultPath);
            string outputPath = output.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (resultDirectory == null ||
                !string.Equals(resultDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    outputPath, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(resultPath) ||
                !string.Equals(Path.GetExtension(resultPath), compatibleMp4 ? ".mp4" : ".mkv",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Couldn't verify the finished video in the selected folder.");
            }

            var file = new FileInfo(resultPath);
            if (file.Length == 0)
                throw new InvalidOperationException("The finished video is empty. Choose another folder and try again.");

            return new DownloadResult { FilePath = resultPath };
        }

        private static List<string> CreateArguments(string url, string output, string toolsDirectory,
            string node, int maxHeight, bool compatibleMp4, int titleLimit, string filePrefix)
        {
            string heightFilter = maxHeight == 0 ? "" :
                "[height<=" + maxHeight.ToString(CultureInfo.InvariantCulture) + "]";
            string format = compatibleMp4
                ? "bv[ext=mp4][vcodec^=avc1]" + heightFilter + "+ba[ext=m4a][acodec^=mp4a]" +
                    "/b[ext=mp4][vcodec^=avc1][acodec^=mp4a]" + heightFilter
                : "bv" + heightFilter + "+ba/b" + heightFilter;

            var args = new List<string>
            {
                "--ignore-config", "--no-plugin-dirs", "--no-cache-dir",
                "--no-playlist", "--match-filters", "!is_live", "--no-overwrites", "--continue",
                "--windows-filenames", "--no-mtime", "--color", "never",
                "--socket-timeout", "20", "--retries", "3", "--fragment-retries", "3", "--abort-on-unavailable-fragments",
                "--encoding", "utf-8",
                "--no-js-runtimes", "--js-runtimes", "node:" + node,
                "--ffmpeg-location", toolsDirectory,
                "--paths", output,
                "--output", filePrefix + "%(title)." + titleLimit.ToString(CultureInfo.InvariantCulture) +
                    "B [%(id)s] [%(height|unknown)sp].%(ext)s",
                "--format", format,
                "--merge-output-format", compatibleMp4 ? "mp4" : "mkv",
                "--remux-video", compatibleMp4 ? "mp4" : "mkv",
                "--no-simulate", "--progress", "--newline", "--progress-delta", "0.4",
                "--progress-template", "download:" + ProgressMarker +
                    "%(progress.status)s|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
                "--progress-template", "postprocess:" + ProcessingMarker + "%(progress.status)s",
                "--print", "after_move:" + FileMarker + "%(filepath)s",
                "--", url
            };
            return args;
        }

        internal static string FindNode(string toolsDirectory)
        {
            var directories = new List<string> { toolsDirectory };
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
                directories.Add(Path.Combine(programFiles, "nodejs"));
            foreach (string part in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                string path = part.Trim().Trim('"');
                if (Path.IsPathRooted(path))
                    directories.Add(path);
            }
            foreach (string directory in directories)
            {
                string node = Path.Combine(directory, "node.exe");
                if (File.Exists(node))
                    return node;
            }
            return null;
        }

        private static void ReadOutput(ConcurrentQueue<string> lines, Queue<string> errors,
            Action<DownloadProgress> progress, ref string filepath)
        {
            string line;
            while (lines.TryDequeue(out line))
            {
                if (line.StartsWith(FileMarker, StringComparison.Ordinal))
                {
                    filepath = line.Substring(FileMarker.Length);
                }
                else if (line.StartsWith(ProgressMarker, StringComparison.Ordinal))
                {
                    string[] fields = line.Substring(ProgressMarker.Length).Split('|');
                    if (fields.Length != 4)
                        continue;
                    double percent;
                    if (!double.TryParse(fields[1].Trim().TrimEnd('%'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out percent) || double.IsNaN(percent))
                    {
                        Report(progress, "Downloading video or audio\u2026", null);
                        continue;
                    }
                    string speed = fields[2].Trim();
                    string eta = fields[3].Trim();
                    string message = "Downloading video or audio\u2026";
                    if (!string.IsNullOrEmpty(speed) && speed != "Unknown" && speed != "NA")
                        message += "  " + speed;
                    if (!string.IsNullOrEmpty(eta) && eta != "Unknown" && eta != "NA")
                        message += "  \u2022  " + eta + " remaining";
                    Report(progress, message, Math.Max(0, Math.Min(100, percent)));
                }
                else if (line.StartsWith(ProcessingMarker, StringComparison.Ordinal))
                {
                    Report(progress, "Preparing the finished video\u2026", null);
                }
                else if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                {
                    if (errors.Count == 3)
                        errors.Dequeue();
                    errors.Enqueue(line.Substring(6).Trim());
                }
            }
        }

        private static void Report(Action<DownloadProgress> handler, string message, double? percent)
        {
            if (handler != null)
                handler(new DownloadProgress { Message = message, Percent = percent });
        }

        internal static bool WaitForToolOutput(Process process, Func<bool> streamsClosed,
            Action readPending, CancellationToken cancellation)
        {
            while (!process.WaitForExit(125))
            {
                cancellation.ThrowIfCancellationRequested();
                readPending();
            }

            // A child can keep a redirected pipe open after the tool exits.
            // Allow time for final output while keeping cancellation responsive.
            var drain = Stopwatch.StartNew();
            while (!streamsClosed() && drain.ElapsedMilliseconds < 5000)
            {
                cancellation.ThrowIfCancellationRequested();
                readPending();
                if (cancellation.WaitHandle.WaitOne(125))
                    cancellation.ThrowIfCancellationRequested();
            }

            bool complete = streamsClosed();
            readPending();
            cancellation.ThrowIfCancellationRequested();
            return complete;
        }

        internal static void StopProcessTree(Process process)
        {
            try
            {
                if (process.HasExited)
                    return;

                var stop = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                    Arguments = "/pid " + process.Id.ToString(CultureInfo.InvariantCulture) + " /t /f",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var killer = Process.Start(stop))
                {
                    if (killer != null)
                        killer.WaitForExit(4000);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // If taskkill is unavailable, still stop the main downloader below.
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Cleanup is best effort. Preserve the failure or cancellation that prompted it.
                }
                catch (InvalidOperationException)
                {
                    // It exited between the check and Kill.
                }
            }
        }

        internal static string JoinArguments(IEnumerable<string> arguments)
        {
            var quoted = new List<string>();
            foreach (var argument in arguments)
                quoted.Add(QuoteArgument(argument));
            return string.Join(" ", quoted.ToArray());
        }

        private static string QuoteArgument(string argument)
        {
            // Windows command-line parsing doubles backslashes before a closing or literal quote.
            var quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    slashes++;
                    continue;
                }
                quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                quoted.Append(c);
                slashes = 0;
            }
            quoted.Append('\\', slashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }
    }
}
