using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using System.Web.Script.Serialization;

namespace CustomYouTubeDownloader
{
    internal sealed class AppPreferences
    {
        private const string SettingsName = "settings.json";
        private const int MaxSettingsChars = 16384;
        private static readonly object SettingsLock = new object();
        private static readonly HashSet<string> Countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "GH", "US", "GB", "CA", "NG", "ZA", "IN", "DE", "FR", "AU"
        };
        private readonly string settingsDirectory;

        public string Folder;
        public string CountryCode;
        public int Quality;
        public int Format;
        public int Captions;
        public bool EnglishAudio;
        public int ParallelDownloads = DefaultParallelDownloads;

        internal const int DefaultParallelDownloads = 3;
        internal const int MaxParallelDownloads = 4;

        internal static string DefaultFolder
        {
            get {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads", "Offline Videos");
            }
        }

        private AppPreferences(string directory)
        {
            settingsDirectory = directory;
            Folder = DefaultFolder;
            CountryCode = "GH";
            Captions = 1;
        }

        public static AppPreferences Load()
        {
            return Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Custom YouTube Downloader"));
        }

        internal static AppPreferences Load(string directory)
        {
            lock (SettingsLock)
            {
                var preferences = new AppPreferences(directory);
                string path = Path.Combine(directory, SettingsName);
                var values = ReadSettings(path) ?? ReadSettings(path + ".bak");
                string previousFolder = NormalizeFolder(ReadText(Path.Combine(directory, "download-folder.txt")));

                if (values == null)
                {
                    preferences.Folder = previousFolder ?? DefaultFolder;
                    return preferences;
                }

                preferences.Folder = NormalizeFolder(TextValue(values, "folder")) ?? previousFolder ?? DefaultFolder;
                preferences.CountryCode = NormalizeCountry(TextValue(values, "country"));
                preferences.Quality = ReadIndex(values, "quality", 5, 0);
                preferences.Format = ReadIndex(values, "format", 2, 0);
                preferences.Captions = ReadIndex(values, "captions", 4, 1);
                object englishAudio;
                preferences.EnglishAudio = values.TryGetValue("englishAudio", out englishAudio) &&
                    englishAudio is bool && (bool)englishAudio;
                object parallelDownloads;
                if (values.TryGetValue("parallelDownloads", out parallelDownloads) && parallelDownloads is int)
                    preferences.ParallelDownloads = ValidParallelDownloads((int)parallelDownloads);
                return preferences;
            }
        }

        public void Save()
        {
            lock (SettingsLock)
            {
                string path = Path.Combine(settingsDirectory, SettingsName);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var values = new Dictionary<string, object> {
                        { "folder", NormalizeFolder(Folder) ?? DefaultFolder },
                        { "country", NormalizeCountry(CountryCode) },
                        { "quality", ValidIndex(Quality, 5, 0) },
                        { "format", ValidIndex(Format, 2, 0) },
                        { "captions", ValidIndex(Captions, 4, 1) },
                        { "englishAudio", EnglishAudio },
                        { "parallelDownloads", ValidParallelDownloads(ParallelDownloads) }
                    };
                    Directory.CreateDirectory(settingsDirectory);
                    byte[] data = Encoding.UTF8.GetBytes(CreateSerializer().Serialize(values));
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(data, 0, data.Length);
                        stream.Flush(true);
                    }
                    if (File.Exists(path))
                    {
                        // Keep a known-good backup if the previous settings were damaged.
                        string backup = ReadSettings(path) != null ? path + ".bak" : null;
                        File.Replace(temporary, path, backup);
                    }
                    else
                    {
                        File.Move(temporary, path);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (SecurityException) { }
                finally
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (SecurityException) { }
                }
            }
        }

        private static Dictionary<string, object> ReadSettings(string path)
        {
            string json = ReadText(path);
            if (String.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var values = CreateSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (values == null || (!values.ContainsKey("folder") && !values.ContainsKey("country") &&
                    !values.ContainsKey("quality") && !values.ContainsKey("format") && !values.ContainsKey("captions") &&
                    !values.ContainsKey("englishAudio") && !values.ContainsKey("parallelDownloads")))
                    return null;
                return values;
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = MaxSettingsChars, RecursionLimit = 8 };
        }

        private static string ReadText(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var buffer = new char[MaxSettingsChars + 1];
                    int count = reader.ReadBlock(buffer, 0, buffer.Length);
                    return count > MaxSettingsChars ? null : new String(buffer, 0, count);
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (SecurityException) { return null; }
        }

        private static string TextValue(IDictionary<string, object> values, string name)
        {
            object value;
            return values.TryGetValue(name, out value) ? value as string : null;
        }

        private static int ReadIndex(IDictionary<string, object> values, string name, int count, int fallback)
        {
            object value;
            return values.TryGetValue(name, out value) && value is int ? ValidIndex((int)value, count, fallback) : fallback;
        }

        private static int ValidIndex(int value, int count, int fallback)
        {
            return value >= 0 && value < count ? value : fallback;
        }

        private static int ValidParallelDownloads(int value)
        {
            return value >= 1 && value <= MaxParallelDownloads ? value : DefaultParallelDownloads;
        }

        private static string NormalizeCountry(string value)
        {
            return value != null && Countries.Contains(value) ? value.ToUpperInvariant() : "GH";
        }

        internal static string NormalizeFolder(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return null;
            string path = value.Trim().Replace('/', '\\');
            bool drivePath = path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\';
            bool uncPath = path.StartsWith(@"\\", StringComparison.Ordinal) &&
                !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal);
            if (!drivePath && !uncPath)
                return null;
            try { return Path.GetFullPath(path); }
            catch (ArgumentException) { return null; }
            catch (IOException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (SecurityException) { return null; }
        }
    }
}
