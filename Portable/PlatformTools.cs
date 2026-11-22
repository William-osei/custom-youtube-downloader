using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace CustomYouTubeDownloader
{
    internal static class PlatformTools
    {
#if PORTABLE
        internal const string DownloadSetup = "Install yt-dlp, FFmpeg (including ffprobe), and Node.js 22 or later.";
        internal const string TranslationSetup = "Install whisper-cli and a multilingual ggml model. Set CYD_WHISPER_MODEL to its full path.";
        internal static StringComparison PathComparison { get { return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal; } }
        internal static StringComparer PathComparer { get { return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal; } }

        internal static string SettingsDirectory
        {
            get
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (OperatingSystem.IsMacOS())
                    return Path.Combine(home, "Library", "Application Support", "Custom YouTube Downloader");
                if (OperatingSystem.IsWindows())
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Custom YouTube Downloader");
                string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                return Path.Combine(!string.IsNullOrEmpty(config) && Path.IsPathFullyQualified(config)
                    ? config : Path.Combine(home, ".config"), "custom-youtube-downloader");
            }
        }

        internal static string ToolsDirectory
        {
            get
            {
                string configured = Environment.GetEnvironmentVariable("CYD_TOOLS_DIR");
                if (!string.IsNullOrEmpty(configured) && Path.IsPathFullyQualified(configured) && Directory.Exists(configured))
                    return Path.GetFullPath(configured);
                string local = Path.Combine(AppContext.BaseDirectory, "tools");
                return Directory.Exists(local) ? local : AppContext.BaseDirectory;
            }
        }

        internal static string Find(string directory, string program)
        {
            string file = OperatingSystem.IsWindows() ? program + ".exe" : program;
            var locations = new List<string> { directory };
            if (program == "whisper-cli") locations.Add(Path.Combine(directory, "whisper"));
            locations.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            // Finder and desktop launchers can inherit a shorter PATH than a terminal.
            if (OperatingSystem.IsMacOS()) locations.Add("/opt/homebrew/bin");
            if (!OperatingSystem.IsWindows())
            {
                locations.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
                locations.Add("/usr/local/bin");
                locations.Add("/usr/bin");
                locations.Add("/bin");
            }
            foreach (string location in locations)
            {
                if (string.IsNullOrEmpty(location) || !Path.IsPathFullyQualified(location)) continue;
                string target = Path.Combine(location, file);
                if (File.Exists(target) && (OperatingSystem.IsWindows() ||
                    (File.GetUnixFileMode(target) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
                    return target;
            }
            return null;
        }

        internal static string WhisperModel(string tools)
        {
            string configured = Environment.GetEnvironmentVariable("CYD_WHISPER_MODEL");
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.IsPathFullyQualified(configured) && File.Exists(configured) ? configured : null;
            foreach (string name in new[] { "ggml-medium-q5_0.bin", "ggml-base.bin" })
                foreach (string directory in new[] { Path.Combine(tools, "whisper"), Path.Combine(SettingsDirectory, "models") })
                {
                    string candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate)) return candidate;
                }
            return null;
        }
#else
        internal const string DownloadSetup = "Run setup-engines.ps1 in the app folder.";
        internal const string TranslationSetup = "Run setup-translation.ps1 in the app folder to install the offline translator.";
        internal const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
        internal static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        internal static string Find(string directory, string program)
        {
            return program == "whisper-cli" ? Path.Combine(directory, "whisper", "whisper-cli.exe") :
                Path.Combine(directory, program + ".exe");
        }

        internal static string WhisperModel(string tools)
        {
            string directory = Path.Combine(tools, "whisper");
            string medium = Path.Combine(directory, "ggml-medium-q5_0.bin");
            return File.Exists(medium) ? medium : Path.Combine(directory, "ggml-base.bin");
        }
#endif
        internal static string FfmpegLocation(string tools)
        {
            string executable = Find(tools, "ffmpeg");
            return executable == null ? tools : Path.GetDirectoryName(executable);
        }

        internal static bool EnhancedModel(string tools)
        {
            string model = WhisperModel(tools);
            return File.Exists(model) && !string.Equals(Path.GetFileName(model), "ggml-base.bin", StringComparison.OrdinalIgnoreCase);
        }

        internal static void SetArguments(ProcessStartInfo start, IEnumerable<string> arguments)
        {
#if PORTABLE
            foreach (string arg in arguments) start.ArgumentList.Add(arg);
#else
            start.Arguments = DownloadWorker.JoinArguments(arguments);
#endif
        }
    }
}
