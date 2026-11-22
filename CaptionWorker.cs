using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CustomYouTubeDownloader
{
    internal enum CaptionMode
    {
        None,
        Original,
        English,
        OriginalAndEnglish
    }

    internal sealed class CaptionResult
    {
        public int Count;
        public string Message;
        public bool MissingRequested;
    }

    internal static class CaptionWorker
    {
        private static readonly Regex TrackTag = new Regex(
            @"\A[a-zA-Z]{2,8}(?:-[a-zA-Z0-9]{1,12}){0,5}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Cue = new Regex(
            @"(?m)^\s*\d{2,}:\d{2}:\d{2},\d{3} --> \d{2,}:\d{2}:\d{2},\d{3}[^\r\n]*\r?\n[^\r\n]+",
            RegexOptions.CultureInvariant);
        private static readonly Regex WhisperProgress = new Regex(
            @"progress\s*=\s*(\d{1,3})%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static Task<CaptionResult> RunAsync(string toolsDirectory, string videoUrl, string videoFile,
            CaptionMode mode, Action<DownloadProgress> progress, CancellationToken token)
        {
            return Task.Run(() => Run(Path.GetFullPath(toolsDirectory), videoUrl, videoFile, mode, progress, token), token);
        }

        // Only the explicit editor action regenerates a valid transcript. A normal download reuses it.
        internal static Task<CaptionResult> RetranslateEnglishAsync(string toolsDirectory, string videoFile,
            Action<DownloadProgress> progress, CancellationToken token, string languageOverride = null)
        {
            return Task.Run(() =>
            {
                string video = Path.GetFullPath(videoFile);
                string destination = Path.ChangeExtension(video, ".en.srt");
                string previous = FileDigest(destination);
                string stage = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cyd-captions-" + Guid.NewGuid().ToString("N"))).FullName;
                string pending = Path.Combine(Path.GetDirectoryName(video), ".cyd-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    string tools = Path.GetFullPath(toolsDirectory);
                    var audio = SelectAudio(ReadMediaTracks(tools, video, token));
                    File.Copy(MakeEnglish(tools, video, stage, audio, null, progress, token, languageOverride), pending);
                    token.ThrowIfCancellationRequested();
                    if (FileDigest(destination) != previous)
                        throw new IOException("The English transcript changed during translation. Reopen it to review your edits.");
                    string message = "English transcript regenerated from the video.";
                    if (previous == null)
                        File.Move(pending, destination);
                    else
                    {
                        string backup = Path.Combine(Path.GetDirectoryName(video), Path.GetFileNameWithoutExtension(video) +
                            ".en.previous-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".srt");
                        File.Replace(pending, destination, backup);
                        message += " Previous transcript saved as " + Path.GetFileName(backup) + ".";
                    }
                    return new CaptionResult { Count = 1, Message = message };
                }
                finally
                {
                    TryDeleteFile(pending);
                    DeleteStageFiles(stage);
                }
            }, token);
        }

        private static string FileDigest(string path)
        {
            if (!File.Exists(path)) return null;
            using (var file = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(file));
        }

        private static CaptionResult Run(string tools, string videoUrl, string videoFile, CaptionMode mode,
            Action<DownloadProgress> progress, CancellationToken token)
        {
            if (mode == CaptionMode.None)
                return new CaptionResult { Message = "" };

            bool wantOriginal = mode == CaptionMode.Original || mode == CaptionMode.OriginalAndEnglish;
            bool wantEnglish = mode == CaptionMode.English || mode == CaptionMode.OriginalAndEnglish;
            if (!wantOriginal && !wantEnglish)
                throw new ArgumentOutOfRangeException("mode");

            var video = new FileInfo(videoFile);
            if (!video.Exists || video.Length == 0)
                throw new FileNotFoundException("The finished video is missing.", video.FullName);

            string stem = Path.Combine(video.DirectoryName, Path.GetFileNameWithoutExtension(video.Name));
            string englishPath = stem + ".en.srt";
            bool englishReady = wantEnglish && HasCues(englishPath);
            bool originalReady = false;
            bool generated = false;
            string sourceLanguage = null;
            string translatedYouTubeTrack = null;
            MediaTrack[] streams = null;
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var issues = new List<string>();
            if (englishReady)
                files.Add(englishPath);

            token.ThrowIfCancellationRequested();
            if (englishReady && !wantOriginal)
                return new CaptionResult { Count = 1, Message = "English subtitles are ready." };

            string stage = Path.Combine(Path.GetTempPath(), "cyd-captions-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                if (!String.IsNullOrWhiteSpace(videoUrl))
                {
                    try
                    {
                        string source = DownloadWorker.NormalizeVideoUrl(videoUrl);
                        string node = DownloadWorker.FindNode(tools);
                        if (node == null || !File.Exists(Path.Combine(tools, "yt-dlp.exe")))
                            throw new InvalidOperationException("The YouTube caption tools are unavailable.");

                        Report(progress, "Looking for YouTube subtitles…", null);
                        var tracks = ReadTracks(tools, node, source, stage, token);
                        sourceLanguage = tracks.Language;
                        string originalTag = wantOriginal ? ChooseOriginal(tracks) : null;
                        if (originalTag != null)
                        {
                            string originalPath = stem + "." + LanguageFileTag(originalTag) + ".srt";
                            if (HasCues(originalPath))
                            {
                                files.Add(originalPath);
                                originalReady = true;
                                if (wantEnglish && IsEnglish(originalTag))
                                    englishReady = true;
                                originalTag = null;
                            }
                        }
                        string englishTag = wantEnglish && !englishReady ? ChooseEnglish(tracks) : null;
                        if (englishTag != null && !tracks.Manual.Contains(englishTag))
                        {
                            streams = ReadMediaTracks(tools, video.FullName, token);
                            if (SaveEmbeddedEnglish(tools, streams, video.FullName, stage, englishPath, token))
                            {
                                englishReady = true;
                                files.Add(englishPath);
                                englishTag = null;
                            }
                        }
                        if (originalTag != null && IsEnglish(originalTag) && englishTag != null)
                            originalTag = englishTag;

                        var requests = new[] { originalTag, englishTag }.Where(tag => tag != null)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        var sourceFiles = FetchTracks(tools, node, source, stage, requests, progress, token);
                        foreach (var tag in requests)
                        {
                            string staged;
                            if (!sourceFiles.TryGetValue(tag, out staged))
                                continue;
                            if (tag == englishTag && tag != originalTag && !tracks.Manual.Contains(tag) &&
                                !tag.EndsWith("-orig", StringComparison.OrdinalIgnoreCase) && !IsEnglish(tracks.Language) &&
                                File.Exists(Path.Combine(tools, "whisper", "ggml-medium-q5_0.bin")))
                            {
                                // Keep YouTube's machine translation as a fallback while translating the original dialogue.
                                translatedYouTubeTrack = staged;
                                continue;
                            }
                            string destination = stem + "." + LanguageFileTag(tag) + ".srt";
                            try
                            {
                                Publish(staged, destination, token);
                                files.Add(destination);
                                if (tag == originalTag)
                                    originalReady = true;
                                if (wantEnglish && IsEnglish(tag))
                                    englishReady = true;
                            }
                            catch (Exception ex)
                            {
                                if (!IsExpectedFailure(ex))
                                    throw;
                                issues.Add(ex.Message);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!IsExpectedFailure(ex))
                            throw;
                        issues.Add(ex.Message);
                    }
                }

                if (wantEnglish && !englishReady)
                {
                    try
                    {
                        if (streams == null)
                        {
                            streams = ReadMediaTracks(tools, video.FullName, token);
                            englishReady = SaveEmbeddedEnglish(tools, streams, video.FullName, stage, englishPath, token);
                        }
                        if (!englishReady)
                        {
                            string translated = MakeEnglish(tools, video.FullName, stage, SelectAudio(streams, sourceLanguage), sourceLanguage, progress, token);
                            Publish(translated, englishPath, token);
                            generated = true;
                        }
                        files.Add(englishPath);
                        englishReady = true;
                    }
                    catch (Exception ex)
                    {
                        if (!IsExpectedFailure(ex))
                            throw;
                        issues.Add(ex.Message);
                    }
                }
                if (!englishReady && translatedYouTubeTrack != null)
                {
                    try
                    {
                        Publish(translatedYouTubeTrack, englishPath, token);
                        files.Add(englishPath);
                        englishReady = true;
                    }
                    catch (Exception ex)
                    {
                        if (!IsExpectedFailure(ex)) throw;
                        issues.Add(ex.Message);
                    }
                }
            }
            finally
            {
                DeleteStageFiles(stage);
            }

            token.ThrowIfCancellationRequested();
            var message = new List<string>();
            if (originalReady && englishReady)
                message.Add(files.Count == 1 ? "Original audio captions are in English." : "Original and English subtitles are ready.");
            else if (originalReady)
                message.Add("Original-language subtitles are ready.");
            else if (englishReady)
                message.Add(generated ? "English subtitles generated from the audio." : "English subtitles are ready.");
            if (originalReady && englishReady && generated)
                message.Add("English subtitles were generated from the audio.");
            if (wantOriginal && !originalReady)
                message.Add("Original-language subtitles were unavailable.");
            if (wantEnglish && !englishReady)
                message.Add("English subtitles could not be saved.");
            bool missingRequested = (wantOriginal && !originalReady) || (wantEnglish && !englishReady);
            if (missingRequested)
            {
                if (issues.Count > 0)
                    message.Add(issues[issues.Count - 1]);
            }
            return new CaptionResult {
                Count = files.Count, Message = String.Join(" ", message.ToArray()), MissingRequested = missingRequested
            };
        }

        private sealed class CaptionTracks
        {
            public string Language;
            public List<string> Manual;
            public List<string> Auto;
        }

        private static CaptionTracks ReadTracks(string tools, string node, string source, string stage, CancellationToken token)
        {
            var args = YouTubeArguments(node);
            args.AddRange(new[] {
                "--write-subs", "--write-auto-subs", "--print", "%(.{language,subtitles,automatic_captions})j", "--", source
            });
            var output = RunProcess(Path.Combine(tools, "yt-dlp.exe"), args, stage, token, null);
            if (output.ExitCode != 0 || String.IsNullOrWhiteSpace(output.Readout))
                throw new InvalidOperationException("YouTube did not return a subtitle list.");

            var json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 64 };
            Dictionary<string, object> data;
            try
            {
                data = json.Deserialize<Dictionary<string, object>>(output.Readout.Trim());
                if (data == null)
                    throw new InvalidOperationException("YouTube did not return a subtitle list.");
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("The YouTube subtitle list could not be read.");
            }

            object rawLanguage;
            data.TryGetValue("language", out rawLanguage);
            string language = rawLanguage as string;
            return new CaptionTracks {
                Language = language != null && TrackTag.IsMatch(language) ? language : null,
                Manual = ReadTags(data, "subtitles"),
                Auto = ReadTags(data, "automatic_captions")
            };
        }

        private static List<string> ReadTags(Dictionary<string, object> data, string field)
        {
            object value;
            if (data.TryGetValue(field, out value))
            {
                var tracks = value as Dictionary<string, object>;
                if (tracks != null)
                    return tracks.Keys.Where(tag => TrackTag.IsMatch(tag) && !tag.Equals("live-chat", StringComparison.OrdinalIgnoreCase))
                        .ToList();
            }
            return new List<string>();
        }

        private static string ChooseEnglish(CaptionTracks tracks)
        {
            return Exact(tracks.Manual, "en") ?? tracks.Manual.FirstOrDefault(IsEnglish) ??
                Exact(tracks.Auto, "en-orig") ??
                tracks.Auto.FirstOrDefault(tag => IsEnglish(tag) && tag.EndsWith("-orig", StringComparison.OrdinalIgnoreCase)) ??
                Exact(tracks.Auto, "en") ?? tracks.Auto.FirstOrDefault(IsEnglish);
        }

        private static string ChooseOriginal(CaptionTracks tracks)
        {
            var originalAutos = tracks.Auto.Where(tag => tag.EndsWith("-orig", StringComparison.OrdinalIgnoreCase)).ToList();
            string native = tracks.Language;
            if (SameLanguage(native, "und"))
                native = null;
            if (native == null && originalAutos.Count > 0)
                native = WithoutOriginalSuffix(originalAutos[0]);
            if (native == null)
                return tracks.Manual.Count == 1 ? tracks.Manual[0] : null;

            return Exact(tracks.Manual, native) ?? tracks.Manual.FirstOrDefault(tag => SameLanguage(tag, native)) ??
                Exact(originalAutos, native + "-orig") ?? originalAutos.FirstOrDefault(tag => SameLanguage(tag, native)) ??
                Exact(tracks.Auto, native);
        }

        private static string Exact(IEnumerable<string> values, string expected)
        {
            return values.FirstOrDefault(tag => tag.Equals(expected, StringComparison.OrdinalIgnoreCase));
        }

        private static bool SameLanguage(string a, string b)
        {
            return a != null && b != null &&
                a.Split('-')[0].Equals(b.Split('-')[0], StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEnglish(string tag)
        {
            return SameLanguage(tag, "en");
        }

        private static string WithoutOriginalSuffix(string tag)
        {
            return tag.EndsWith("-orig", StringComparison.OrdinalIgnoreCase) ? tag.Substring(0, tag.Length - 5) : tag;
        }

        private static string LanguageFileTag(string tag)
        {
            return IsEnglish(tag) ? "en" : WithoutOriginalSuffix(tag).ToLowerInvariant();
        }

        private static Dictionary<string, string> FetchTracks(string tools, string node, string source, string stage,
            string[] tags, Action<DownloadProgress> progress, CancellationToken token)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (tags.Length == 0)
                return files;

            var args = YouTubeArguments(node);
            args.AddRange(new[] {
                "--ignore-errors", "--write-subs", "--write-auto-subs", "--sub-langs", String.Join(",", tags),
                "--sub-format", "vtt/srt/best", "--ffmpeg-location", tools, "--convert-subs", "srt",
                "--output", Path.Combine(stage, "caption.%(ext)s"), "--", source
            });
            Report(progress, "Saving available subtitle tracks…", null);
            var result = RunProcess(Path.Combine(tools, "yt-dlp.exe"), args, stage, token, null);
            foreach (string tag in tags)
            {
                string file = Path.Combine(stage, "caption." + tag + ".srt");
                if (HasCues(file))
                    files.Add(tag, file);
            }
            if (files.Count == 0 && result.ExitCode != 0)
                throw new InvalidOperationException("YouTube did not deliver those subtitle tracks.");
            return files;
        }

        private static List<string> YouTubeArguments(string node)
        {
            return new List<string> {
                "--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--skip-download", "--no-playlist",
                "--no-js-runtimes", "--js-runtimes", "node:" + node, "--encoding", "utf-8",
                "--socket-timeout", "15", "--retries", "1", "--extractor-retries", "1", "--color", "never"
            };
        }

        private sealed class MediaTracks { public MediaTrack[] streams { get; set; } }

        private sealed class MediaTrack
        {
            public int index { get; set; }
            public string codec_type { get; set; }
            public string codec_name { get; set; }
            public Dictionary<string, int> disposition { get; set; }
            public Dictionary<string, string> tags { get; set; }

            internal bool Has(string flag)
            {
                int value;
                return disposition != null && disposition.TryGetValue(flag, out value) && value != 0;
            }

            internal string Tag(string name)
            {
                return tags == null ? null : tags.FirstOrDefault(t => t.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            }

            internal bool Commentary
            {
                get { return Has("comment") || Labeled(@"\b(commentary|audio description|descriptive audio)\b"); }
            }

            internal bool Labeled(string pattern)
            {
                return Regex.IsMatch(Tag("title") + " " + Tag("handler_name"), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            internal string Map { get { return "0:" + index.ToString(CultureInfo.InvariantCulture); } }
        }

        private static MediaTrack[] ReadMediaTracks(string tools, string file, CancellationToken token)
        {
            string probe = Path.Combine(tools, "ffprobe.exe");
            if (File.Exists(probe))
            {
                try
                {
                    var result = RunProcess(probe, new[] { "-v", "error", "-show_entries",
                        "stream=index,codec_type,codec_name:stream_disposition:stream_tags=language,title,handler_name", "-of", "json", file
                    }, tools, token, null);
                    if (result.ExitCode == 0)
                    {
                        var media = new JavaScriptSerializer().Deserialize<MediaTracks>(result.Readout);
                        if (media != null && media.streams != null) return media.streams;
                    }
                }
                catch (Exception ex)
                {
                    if (!IsExpectedFailure(ex) && !(ex is ArgumentException)) throw;
                }
            }
            return new MediaTrack[0];
        }

        private static MediaTrack SelectAudio(IEnumerable<MediaTrack> streams, string sourceLanguage = null)
        {
            string native = WhisperLanguage(sourceLanguage);
            return streams.Where(s => s.codec_type == "audio")
                .OrderBy(s => s.Commentary || s.Has("visual_impaired") || s.Has("hearing_impaired") || s.Has("karaoke"))
                .ThenByDescending(s => s.Has("original") || s.Labeled(@"\boriginal\b"))
                .ThenBy(s => s.Has("dub") || s.Labeled(@"\b(dub|dubbed)\b"))
                .ThenByDescending(s => native != null && WhisperLanguage(s.Tag("language")) == native)
                .ThenByDescending(s => s.Has("default")).ThenBy(s => s.index).FirstOrDefault();
        }

        internal static string OriginalAudioMap(string toolsDirectory, string videoFile, CancellationToken token)
        {
            var track = SelectAudio(ReadMediaTracks(toolsDirectory, videoFile, token));
            return track == null ? "0:a:0" : track.Map;
        }

        private static bool SaveEmbeddedEnglish(string tools, IEnumerable<MediaTrack> streams, string video,
            string stage, string destination, CancellationToken token)
        {
            // Forced captions frequently contain signs and foreign phrases only. They are incomplete as a dub script.
            var codecs = new[] { "subrip", "ass", "ssa", "webvtt", "mov_text", "text" };
            var tracks = streams.Where(s => s.codec_type == "subtitle" && codecs.Contains(s.codec_name) &&
                WhisperLanguage(s.Tag("language")) == "en" && !s.Has("forced") && !s.Commentary &&
                !s.Labeled(@"\b(forced|signs only|signs (and|&) songs)\b")).OrderByDescending(s => s.Has("default"));
            foreach (var track in tracks)
            {
                string srt = Path.Combine(stage, "embedded-" + track.index.ToString(CultureInfo.InvariantCulture) + ".srt");
                var read = RunProcess(Path.Combine(tools, "ffmpeg.exe"), new[] {
                    "-nostdin", "-loglevel", "error", "-y", "-i", video, "-map", track.Map, "-c:s", "srt", srt
                }, stage, token, null);
                if (read.ExitCode != 0 || !HasCues(srt)) continue;
                Publish(srt, destination, token);
                return true;
            }
            return false;
        }

        // Language tokens in the multilingual medium and base checkpoints shipped by the installer.
        private static readonly HashSet<string> WhisperLanguages = new HashSet<string>(
            ("en zh de es ru ko fr ja pt tr pl ca nl ar sv it id hi fi vi he uk el ms cs ro da hu ta no th ur hr " +
             "bg lt la mi ml cy sk te fa lv bn sr az sl kn et mk br eu is hy ne mn bs kk sq sw gl mr pa si km sn " +
             "yo so af oc ka be tg sd gu am yi lo uz fo ht ps tk nn mt sa lb my bo tl mg as tt haw ln ha ba jw su").Split(' '));

        internal static string[] SourceLanguageCodes
        {
            get { return WhisperLanguages.OrderBy(code => code, StringComparer.Ordinal).ToArray(); }
        }

        private static string WhisperLanguage(string tag)
        {
            if (String.IsNullOrWhiteSpace(tag)) return null;
            string code = tag.Split('-', '_')[0].ToLowerInvariant();
            switch (code)
            {
                case "fre": return "fr"; case "ger": return "de"; case "dut": return "nl"; case "cze": return "cs";
                case "chi": return "zh"; case "gre": return "el"; case "per": return "fa"; case "rum": return "ro";
                case "wel": return "cy"; case "slo": return "sk"; case "alb": return "sq"; case "arm": return "hy";
                case "baq": return "eu"; case "bur": return "my"; case "geo": return "ka"; case "ice": return "is";
                case "mac": return "mk"; case "mao": return "mi"; case "may": return "ms"; case "tib": return "bo";
                case "fil": return "tl"; case "jv": case "jav": return "jw"; case "nb": case "nob": return "no";
            }
            if (WhisperLanguages.Contains(code)) return code;
            if (code.Length == 3)
            {
                var language = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                    .FirstOrDefault(c => c.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase));
                if (language != null && WhisperLanguages.Contains(language.TwoLetterISOLanguageName)) return language.TwoLetterISOLanguageName;
            }
            return null;
        }

        private static string MakeEnglish(string tools, string videoFile, string stage, MediaTrack track, string sourceLanguage,
            Action<DownloadProgress> progress, CancellationToken token, string languageOverride = null)
        {
            string explicitLanguage = WhisperLanguage(languageOverride);
            if (!String.IsNullOrWhiteSpace(languageOverride) && explicitLanguage == null)
                throw new ArgumentException("Choose a supported spoken language for translation.");
            string whisperHome = Path.Combine(tools, "whisper");
            string whisper = Path.Combine(whisperHome, "whisper-cli.exe");
            string model = Path.Combine(whisperHome, "ggml-medium-q5_0.bin");
            bool enhanced = File.Exists(model);
            if (!enhanced) model = Path.Combine(whisperHome, "ggml-base.bin");
            string ffmpeg = Path.Combine(tools, "ffmpeg.exe");
            if (!File.Exists(whisper) || !File.Exists(model))
                throw new InvalidOperationException("Run setup-translation.ps1 in the app folder to install the offline translator.");
            if (!File.Exists(ffmpeg))
                throw new InvalidOperationException("FFmpeg is required to read this video’s audio.");

            string audio = Path.Combine(stage, "speech.wav");
            string captions = Path.Combine(stage, "english");
            Report(progress, "Preparing the video’s audio…", null);
            var extracted = RunProcess(ffmpeg, new[] {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-i", videoFile, "-map", track == null ? "0:a:0" : track.Map,
                "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-rf64", "auto", audio
            }, stage, token, null);
            if (extracted.ExitCode != 0 || !File.Exists(audio) || new FileInfo(audio).Length <= 80)
                throw new InvalidOperationException("The video’s audio could not be read for translation.");

            string language = explicitLanguage ?? WhisperLanguage(track == null ? null : track.Tag("language")) ??
                WhisperLanguage(sourceLanguage) ?? "auto";
            string status = enhanced ? "Translating dialogue with Whisper medium…" : "Translating dialogue with Whisper base…";
            Report(progress, status, null);
            var translated = RunProcess(whisper, new[] {
                "--model", model, "--file", audio, "--language", language, "--translate", "--beam-size", "8", "--best-of", "8", "--output-srt",
                "--output-file", captions, "--no-gpu", "--print-progress"
            }, whisperHome, token, line =>
            {
                var match = WhisperProgress.Match(line);
                if (match.Success)
                {
                    int percent = Int32.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    Report(progress, status, Math.Min(percent, 100));
                }
            });
            string staged = captions + ".srt";
            if (translated.ExitCode != 0)
                throw new InvalidOperationException("The English speech translator could not finish.");
            if (!HasCues(staged))
                throw new InvalidOperationException("No speech could be turned into English subtitles.");
            return staged;
        }

        internal sealed class ProcessOutput
        {
            public int ExitCode;
            public string Readout;
        }

        internal static ProcessOutput RunProcess(string file, IEnumerable<string> args, string workingDirectory,
            CancellationToken token, Action<string> onLine)
        {
            using (var process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = file, Arguments = DownloadWorker.JoinArguments(args), WorkingDirectory = workingDirectory,
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                }
            })
            {
                const int maxReadout = 32 * 1024 * 1024;
                var readout = new StringBuilder();
                var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
                int closedStreams = 0;
                process.OutputDataReceived += (sender, item) =>
                {
                    if (item.Data == null)
                    {
                        Interlocked.Increment(ref closedStreams);
                        return;
                    }
                    if (readout.Length + item.Data.Length < maxReadout)
                        readout.AppendLine(item.Data);
                    if (onLine != null)
                        lines.Enqueue(item.Data);
                };
                process.ErrorDataReceived += (sender, item) =>
                {
                    if (item.Data == null)
                        Interlocked.Increment(ref closedStreams);
                    else if (onLine != null)
                        lines.Enqueue(item.Data);
                };
                bool started = false;
                token.ThrowIfCancellationRequested();
                try
                {
                    started = process.Start();
                    if (!started)
                        throw new InvalidOperationException("A subtitle tool could not start.");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    bool outputComplete = DownloadWorker.WaitForToolOutput(process, () => Volatile.Read(ref closedStreams) == 2,
                        () => ReadProgress(lines, onLine), token);
                    if (process.ExitCode == 0 && !outputComplete)
                        throw new InvalidOperationException("A subtitle tool stopped before closing its output.");
                    return new ProcessOutput {
                        ExitCode = process.ExitCode, Readout = outputComplete ? readout.ToString() : ""
                    };
                }
                finally
                {
                    if (started)
                        DownloadWorker.StopProcessTree(process);
                }
            }
        }

        private static void ReadProgress(System.Collections.Concurrent.ConcurrentQueue<string> lines, Action<string> onLine)
        {
            string line;
            while (lines.TryDequeue(out line))
                onLine(line);
        }

        private static bool HasCues(string path)
        {
            if (!File.Exists(path))
                return false;
            try
            {
                using (var reader = new StreamReader(path, Encoding.UTF8, true))
                {
                    var sample = new char[16384];
                    int count = reader.ReadBlock(sample, 0, sample.Length);
                    return Cue.IsMatch(new String(sample, 0, count));
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void Publish(string source, string destination, CancellationToken token)
        {
            if (HasCues(destination))
                return;
            if (File.Exists(destination))
                throw new InvalidOperationException("A subtitle file already uses the destination name.");

            string stage = Path.Combine(Path.GetDirectoryName(destination), ".cyd-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(source, stage, false);
                token.ThrowIfCancellationRequested();
                File.Move(stage, destination);
            }
            finally
            {
                TryDeleteFile(stage);
            }
        }

        private static void DeleteStageFiles(string stage)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(stage))
                    TryDeleteFile(file);
                Directory.Delete(stage, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void TryDeleteFile(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool IsExpectedFailure(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException ||
                ex is System.ComponentModel.Win32Exception;
        }

        private static void Report(Action<DownloadProgress> progress, string message, double? percent)
        {
            if (progress != null)
                progress(new DownloadProgress { Message = message, Percent = percent });
        }
    }
}
