using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Security.Cryptography;
#if !PORTABLE
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
#endif
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CustomYouTubeDownloader
{
    internal enum NarrationStyle { Faithful, Serious, Sarcastic }

    internal static class EnglishDubWorker
    {
        internal const string Suffix = " [English dub]";
        private static readonly Regex DubEnding = new Regex(
            @" \[(?:(?:Serious|Sarcastic) English dub|English dub)\](?: \((?:[2-9]|[1-9][0-9]+)\))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private const int SampleRate = 22050;
        private static readonly byte[] Silence = new byte[32768];
        // Translation and voice rendering share the laptop's CPU; downloads can continue independently.
        private static readonly SemaphoreSlim RenderSlot = new SemaphoreSlim(1, 1);
        private static readonly Regex Timing = new Regex(
            @"^(?<start>\d{2,}:[0-5]\d:[0-5]\d[,.]\d{3})\s*-->\s*(?<end>\d{2,}:[0-5]\d:[0-5]\d[,.]\d{3})(?:\s.*)?$");
        private static readonly Regex Markup = new Regex(
            @"</?(?:b|i|u|font|c|v|lang|ruby|rt)(?=[. >])[^>]*>|<\d+:\d{2}(?::\d{2})?[.]\d{3}>|\{\\[^}]*\}|[♪♫]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SoundCue = new Regex(
            @"\[(?:(?:(?:soft|upbeat|dramatic|background) )?music(?: playing)?|applause|clapping|cheering|laughter|laughs?|sighs?|gasps?|coughs?|inaudible|indistinct(?: chatter)?|background (?:noise|chatter)|footsteps|phone (?:ringing|rings)|silence|speaking (?:in )?(?:another language|English|French|Spanish|Portuguese|Japanese|Korean|Hindi|Arabic|German|Russian|Mandarin|Italian|Chinese))\]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Whitespace = new Regex(@"\s+");

        internal static bool IsDub(string stem) { return DubEnding.IsMatch(stem); }
        internal static string SourceStem(string stem) { return DubEnding.Replace(stem, ""); }

        private static string FileSuffix(NarrationStyle style)
        {
            switch (style)
            {
                case NarrationStyle.Faithful: return Suffix;
                case NarrationStyle.Serious: return " [Serious English dub]";
                case NarrationStyle.Sarcastic: return " [Sarcastic English dub]";
                default: throw new ArgumentOutOfRangeException("style");
            }
        }

        internal static async Task<string> RunAsync(string toolsDirectory, string sourceUrl, string videoFile,
            NarrationStyle style, Action<DownloadProgress> progress, CancellationToken token)
        {
            string video = Path.GetFullPath(videoFile);
            string tools = Path.GetFullPath(toolsDirectory);
            string ext = Path.GetExtension(video).ToLowerInvariant();
            if ((ext != ".mkv" && ext != ".mp4") || IsDub(Path.GetFileNameWithoutExtension(video)))
                throw new InvalidOperationException("Select an original MP4 or MKV video to create English audio.");
            if (!File.Exists(video))
                throw new FileNotFoundException("The original video could not be found.", video);
            if (!File.Exists(PlatformTools.Find(tools, "ffmpeg")) || !File.Exists(PlatformTools.Find(tools, "ffprobe")))
                throw new InvalidOperationException("English audio needs FFmpeg and FFprobe. " + PlatformTools.DownloadSetup);
            if (RenderSlot.CurrentCount == 0) Report(progress, "Waiting for the English audio renderer…", null);
            await RenderSlot.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var captions = await CaptionWorker.RunAsync(tools, sourceUrl, video, CaptionMode.English, progress, token).ConfigureAwait(false);
                if (captions.MissingRequested || captions.Count == 0)
                    throw new InvalidOperationException(captions.Message + " " + PlatformTools.TranslationSetup);
                return await Task.Run(() => Render(tools, video, ext, style, progress, token), token).ConfigureAwait(false);
            }
            finally { RenderSlot.Release(); }
        }

        private static string Render(string tools, string video, string ext, NarrationStyle style,
            Action<DownloadProgress> progress, CancellationToken token)
        {
            string stem = Path.Combine(Path.GetDirectoryName(video), Path.GetFileNameWithoutExtension(video));
            string captions = stem + ".en.srt";
            string dubStem = stem + FileSuffix(style);
            Report(progress, "Checking the original video and English transcript…", null);
            // Keep the files used for speech and the fingerprint stable through publication.
            using (var input = File.OpenRead(video))
            using (var transcript = File.OpenRead(captions))
            {
                var source = Probe(tools, video, token);
                if (source.Count("video") == 0 || source.Count("audio") == 0 || source.Duration <= 0)
                    throw new InvalidDataException("The original file needs a video track, an audio track and a known duration.");
                string signature = "CYD English dub v2 " + style + " " + Digest(input, token) + " " + Digest(transcript, token);
                bool existing;
                string target = FindOutput(tools, dubStem, ext, source, signature, token, out existing);
                if (existing) return target;

                var cues = ReadCues(captions, source.Duration, token);
                string id = Guid.NewGuid().ToString("N");
                string stage = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cyd-voice-" + id)).FullName;
                // Explicit container format lets the Library ignore the partial file until File.Move commits it.
                string pending = Path.Combine(Path.GetDirectoryName(video), ".cyd-" + id + ".tmp");
                try
                {
                    string pcm = Path.Combine(stage, "voice.pcm");
                    SpeakTimeline(tools, stage, pcm, cues, source.Duration, style, progress, token);
                    Report(progress, "Adding the English voice and preserving the original audio…", null);
                    var args = new List<string> {
                        "-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-i", video,
                        "-f", "s16le", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "1", "-i", pcm, "-i", captions,
                        "-map", "0:v", "-map", "1:a:0", "-map", "0:a", "-map", "0:s?", "-map", "2:s:0",
                        "-map_metadata", "0", "-map_chapters", "0", "-c", "copy", "-c:a:0", "aac", "-b:a:0", "96k",
                        "-metadata", "comment=" + signature, "-metadata:s:a:0", "language=eng",
                        "-metadata:s:a:0", "title=English - " + style, "-metadata:s:a:0", "handler_name=English - " + style,
                        "-disposition:a:0", "default+dub"
                    };
                    if (ext == ".mkv") args.AddRange(new[] { "-map", "0:t?" });
                    if (source.Count("audio") == 1)
                        args.AddRange(new[] { "-metadata:s:a:1", "title=Original audio", "-metadata:s:a:1", "handler_name=Original audio" });
                    for (int i = 1; i <= source.Count("audio"); i++)
                        args.AddRange(new[] { "-disposition:a:" + i, "-default" });
                    string sub = "s:" + source.Count("subtitle");
                    args.AddRange(new[] { "-c:" + sub, ext == ".mp4" ? "mov_text" : "srt", "-metadata:s:" + sub,
                        "language=eng", "-disposition:" + sub, "0", "-f", ext == ".mp4" ? "mp4" : "matroska", pending });
                    var result = CaptionWorker.RunProcess(PlatformTools.Find(tools, "ffmpeg"), args, stage, token, null);
                    if (result.ExitCode != 0 || !IsMatchingDub(Probe(tools, pending, token), source, signature))
                        throw new InvalidOperationException("The English video could not be verified. Check available disk space and retry.");
                    token.ThrowIfCancellationRequested();
                    for (;;)
                    {
                        try { File.Move(pending, target); break; }
                        catch (IOException)
                        {
                            if (!File.Exists(target)) throw;
                            target = FindOutput(tools, dubStem, ext, source, signature, token, out existing);
                            if (existing) break;
                        }
                    }
                    return target;
                }
                finally
                {
                    TryDelete(pending);
                    foreach (string name in new[] { "voice.pcm", "fast-input.pcm", "fast-output.pcm" })
                        TryDelete(Path.Combine(stage, name));
                    try { Directory.Delete(stage); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }

        // Match a previous render by input, transcript and style. Keep earlier revisions, user files,
        // and pre-1.3 dubs playable even when a corrected transcript needs the same display name.
        private static string FindOutput(string tools, string stem, string ext, MediaInfo source, string signature,
            CancellationToken token, out bool existing)
        {
            var occupied = new HashSet<int>();
            string name = Path.GetFileName(stem);
            foreach (string candidate in Directory.EnumerateFiles(Path.GetDirectoryName(stem)))
            {
                token.ThrowIfCancellationRequested();
                if (!string.Equals(Path.GetExtension(candidate), ext, StringComparison.OrdinalIgnoreCase)) continue;
                string file = Path.GetFileNameWithoutExtension(candidate);
                if (!file.StartsWith(name, PlatformTools.PathComparison)) continue;
                string tail = file.Substring(name.Length);
                int number = 1;
                if (tail.Length != 0 && (!tail.StartsWith(" (", StringComparison.Ordinal) ||
                    !tail.EndsWith(")", StringComparison.Ordinal) || tail.Length < 4 ||
                    !int.TryParse(tail.Substring(2, tail.Length - 3), NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
                    number < 2 || tail != " (" + number.ToString(CultureInfo.InvariantCulture) + ")")) continue;
                occupied.Add(number);
                if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    if (IsMatchingDub(Probe(tools, candidate, token), source, signature))
                    {
                        existing = true;
                        return candidate;
                    }
                }
                catch (InvalidDataException) { /* A user file may be unrelated or unreadable. */ }
            }
            for (int next = 1; ; next++)
            {
                string target = stem + (next == 1 ? "" : " (" + next.ToString(CultureInfo.InvariantCulture) + ")") + ext;
                if (occupied.Contains(next) || File.Exists(target) || Directory.Exists(target)) continue;
                existing = false;
                return target;
            }
        }

        private sealed class MediaInfo
        {
            public StreamInfo[] streams { get; set; }
            public FormatInfo format { get; set; }
            internal int Count(string type) { return streams.Count(s => s.codec_type == type); }
            internal double Duration
            {
                get
                {
                    double value;
                    return double.TryParse(format.duration, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                        !double.IsNaN(value) && !double.IsInfinity(value) ? value : 0;
                }
            }
        }
        private sealed class StreamInfo
        {
            public string codec_type { get; set; }
            public Dictionary<string, int> disposition { get; set; }
            public Dictionary<string, string> tags { get; set; }
        }
        private sealed class FormatInfo
        {
            public string duration { get; set; }
            public Dictionary<string, string> tags { get; set; }
        }

        private static MediaInfo Probe(string tools, string path, CancellationToken token)
        {
            var result = CaptionWorker.RunProcess(PlatformTools.Find(tools, "ffprobe"), new[] {
                "-v", "error", "-show_entries", "format=duration:format_tags=comment:stream=codec_type:stream_tags=language:stream_disposition=default",
                "-of", "json", path
            }, tools, token, null);
            if (result.ExitCode == 0)
            {
                var info = new JavaScriptSerializer().Deserialize<MediaInfo>(result.Readout);
                if (info != null && info.format != null && info.streams != null) return info;
            }
            throw new InvalidDataException("The video tracks could not be read in " + Path.GetFileName(path) + ".");
        }

        private static string Tag(Dictionary<string, string> tags, string key)
        {
            return tags == null ? null : tags.FirstOrDefault(t => key.Equals(t.Key, StringComparison.OrdinalIgnoreCase)).Value;
        }

        private static bool IsMatchingDub(MediaInfo dub, MediaInfo original, string signature)
        {
            var audio = dub.streams.Where(s => s.codec_type == "audio").ToArray();
            return Tag(dub.format.tags, "comment") == signature && Math.Abs(dub.Duration - original.Duration) < 0.3 &&
                dub.Count("video") == original.Count("video") && dub.Count("subtitle") == original.Count("subtitle") + 1 &&
                audio.Length == original.Count("audio") + 1 && Tag(audio[0].tags, "language") == "eng" &&
                audio[0].disposition["default"] == 1 && audio.Skip(1).All(s => s.disposition["default"] == 0);
        }

        private static string Digest(Stream stream, CancellationToken token)
        {
            using (var digest = SHA256.Create())
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    digest.TransformBlock(buffer, 0, read, null, 0);
                }
                digest.TransformFinalBlock(buffer, 0, 0);
                return BitConverter.ToString(digest.Hash).Replace("-", "");
            }
        }

        private sealed class Cue
        {
            internal long Start;
            internal long End;
            internal string Text;
            internal string[] Lines;
        }

        private static List<Cue> ReadCues(string path, double duration, CancellationToken token)
        {
            long end = (long)Math.Round(duration * SampleRate);
            var cues = new List<Cue>();
            foreach (string block in Regex.Split(File.ReadAllText(path).Trim(), @"\r?\n[ \t]*\r?\n"))
            {
                token.ThrowIfCancellationRequested();
                var lines = block.Split('\n');
                int n = Array.FindIndex(lines, line => line.Contains("-->"));
                if (n < 0) throw new InvalidDataException("An English subtitle is missing its timing line.");
                var time = Timing.Match(lines[n].Trim());
                if (!time.Success) throw new InvalidDataException("The English subtitle timing could not be read.");
                string[] dialogue = lines.Skip(n + 1).Select(line =>
                    Whitespace.Replace(SoundCue.Replace(Markup.Replace(WebUtility.HtmlDecode(line), " "), " ")
                        .Replace("[", "").Replace("]", ""), " ").Trim())
                    .Where(line => line.Length > 0).ToArray();
                string text = string.Join(" ", dialogue);
                if (text.Length == 0 || !text.Any(char.IsLetterOrDigit)) continue;
                var cue = new Cue { Start = SampleAt(time.Groups["start"].Value),
                    End = Math.Min(end, SampleAt(time.Groups["end"].Value)), Text = text, Lines = dialogue };
                if (cue.Start >= cue.End)
                    throw new InvalidDataException("An English subtitle falls outside the video or has an empty time range.");
                cues.Add(cue);
            }
            if (cues.Count == 0) throw new InvalidDataException("The English subtitles contain no spoken text.");
            // Rolling captions can overlap by a few frames throughout an entire film. Preserve each
            // new start time instead of merging the whole chain into one unsynchronized utterance.
            var merged = new List<Cue>();
            string[] lastDisplayed = new string[0];
            long lastEnd = -1;
            foreach (var cue in cues.OrderBy(c => c.Start))
            {
                var previous = merged.LastOrDefault();
                if (previous != null && cue.Start <= lastEnd + SampleRate / 20 &&
                    (lastDisplayed.Length > 1 || cue.Lines.Length > 1))
                {
                    // In two-line rolling subtitles the bottom line becomes the top line of the next cue.
                    int carried = Math.Min(lastDisplayed.Length, cue.Lines.Length);
                    while (carried > 0 && !lastDisplayed.Skip(lastDisplayed.Length - carried)
                        .SequenceEqual(cue.Lines.Take(carried), StringComparer.OrdinalIgnoreCase))
                        carried--;
                    cue.Text = string.Join(" ", cue.Lines.Skip(carried));
                }
                lastDisplayed = cue.Lines;
                lastEnd = cue.End;
                if (cue.Text.Length == 0)
                {
                    previous.End = Math.Max(previous.End, cue.End);
                    continue;
                }
                if (previous != null && cue.Start <= previous.End)
                {
                    if (cue.Text == previous.Text && (cue.Start < previous.End || previous.End - previous.Start < SampleRate / 4))
                    {
                        previous.End = Math.Max(previous.End, cue.End);
                        continue;
                    }
                    // Near-simultaneous subtitles share one narrator. Some automatic tracks also emit
                    // a very short copy immediately before the full line appears.
                    if (cue.Start - previous.Start < SampleRate / 4)
                    {
                        if (cue.Text.StartsWith(previous.Text + " ", StringComparison.Ordinal))
                            previous.Text = cue.Text;
                        else
                            previous.Text += " " + cue.Text;
                        previous.End = Math.Max(previous.End, cue.End);
                        continue;
                    }
                    if (cue.Start < previous.End)
                    {
                        if (cue.Text.StartsWith(previous.Text + " ", StringComparison.Ordinal))
                            cue.Text = cue.Text.Substring(previous.Text.Length + 1);
                        previous.End = cue.Start;
                    }
                }
                merged.Add(cue);
            }
            var phrases = new List<Cue>();
            foreach (var cue in merged)
            {
                var prev = phrases.LastOrDefault();
                // Automatic captions can split a spoken phrase into tiny pieces. Read adjacent
                // fragments in one breath, keeping sentence boundaries and the surrounding time window.
                if (prev != null && cue.Start - prev.End < SampleRate / 8 &&
                    Math.Min(prev.End - prev.Start, cue.End - cue.Start) < 3 * SampleRate / 2 &&
                    cue.End - prev.Start <= 6 * SampleRate && char.IsLower(cue.Text[0]) &&
                    char.IsLetterOrDigit(prev.Text[prev.Text.Length - 1]) && !prev.Text.StartsWith("-", StringComparison.Ordinal))
                {
                    prev.Text += " " + cue.Text;
                    prev.End = cue.End;
                }
                else phrases.Add(cue);
            }
            return phrases;
        }

        private static long SampleAt(string timestamp)
        {
            var parts = timestamp.Split(':', ',', '.');
            long milliseconds = checked(((long.Parse(parts[0], CultureInfo.InvariantCulture) * 60 +
                int.Parse(parts[1], CultureInfo.InvariantCulture)) * 60 + int.Parse(parts[2], CultureInfo.InvariantCulture)) *
                1000 + int.Parse(parts[3], CultureInfo.InvariantCulture));
            return checked((milliseconds * SampleRate + 500) / 1000);
        }

        // Optional asides react to specific story beats in the translated dialogue. They never
        // replace the speaker's words, and a tightly edited scene can have no asides at all.
        private sealed class Aside
        {
            internal readonly Regex Trigger;
            internal readonly string Line;
            internal Aside(string pattern, string line)
            {
                Trigger = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                Line = line;
            }
        }

        private static readonly Aside[] StoryBeats = {
            new Aside(@"\b(?:we|i|they|she|he)(?:'ve|'s| have| has)? (?:just )?missed (?:the|our|my|that|his|her) (?<ride>train|bus|ferry|flight)\b",
                "How considerate of the {ride} to leave more time for sightseeing."),
            new Aside(@"\b(?:lost|misplaced|forgot|can't find|cannot find|where (?:are|is))\s+(?:the |our |my |your |his |her )?(?<object>keys?|map|compass|ticket|remote|password)\b",
                "Apparently the {object} took some personal time."),
            new Aside(@"\b(?:door (?:is |was )?(?:locked|stuck)|door (?:won't|wouldn't|will not) (?:open|budge))\b",
                "The door has a very strict guest list."),
            new Aside(@"\b(?:what could (?:possibly )?go wrong|nothing can (?:possibly )?go wrong)\b",
                "That sentence has quite a reputation."),
            new Aside(@"\b(?:take|took|found|know) (?:a |the |this )shortcut\b",
                "A shortcut. Geography's favorite little sales pitch."),
            new Aside(@"\b(?:trust me on this|i have a (?:brilliant |cunning |foolproof )plan)\b",
                "Confidence has volunteered as the first piece of evidence."),
            new Aside(@"\b(?:battery (?:is |was )?(?:dead|flat)|(?:phone|laptop) (?:is )?out of (?:power|battery))\b",
                "A masterclass in choosing when to switch off."),
            new Aside(@"\b(?:set off|triggered|tripped) (?:the |an? )?alarm\b",
                "So much for making a quiet entrance."),
            new Aside(@"\b(?:cake|toast|pie) (?:is |was )?(?:burnt|burned)|\b(?:burnt|burned) the (?:cake|toast|pie)\b",
                "The recipe did leave some room for interpretation."),
            new Aside(@"\b(?:waited|(?:been )?waiting|stood in line) for (?:an hour|hours|ages|ever)\b",
                "The waiting certainly got its money's worth."),
            new Aside(@"\b(?:back of the (?:line|queue)|(?:stuck|standing|waiting) in (?:the|a|this) (?:line|queue)|(?:a long|an enormous) queue)\b",
                "A fine opportunity to study the back of somebody's coat."),
            new Aside(@"\b(?:wrong turn(?:ing)?|(?:going|heading|driving|headed) (?:in )?the wrong (?:way|direction))\b",
                "At least the scenic route has found its audience."),
            new Aside(@"\b(?<ride>train|bus|ferry|flight)(?:'s| is| was| has been| got)? (?:delayed|running late)\b",
                "The {ride} seems to regard the timetable as a suggestion."),
            new Aside(@"\b(?:try|tried|trying|attempt(?:ed)?) (?:it |again )?(?:for )?(?:a |the )?(?<attempt>third|fourth|fifth|sixth|seventh|eighth|ninth|tenth) time\b|\b(?:the |our |my )(?<attempt>third|fourth|fifth|sixth|seventh|eighth|ninth|tenth) (?:attempt|try)\b",
                "Apparently the {attempt} attempt gets a speaking part."),
            new Aside(@"\b(?:so much for (?:the|that) (?:forecast|sunshine)|(?:just then |and now |of course,? )it (?:started|starts|began|begins) (?:raining|to rain))\b",
                "The weather appears to have revised the script."),
            new Aside(@"\b(?:awkward silence|(?:nobody|no one) (?:said|says) a word|you could hear a pin drop)\b",
                "The silence is really carrying this conversation."),
            new Aside(@"\b(?:only |all )?(?:we|i|you) (?:only )?have (?:only )?(?<amount>(?:\d{1,2}|two|three|four|five|ten|fifteen|twenty|thirty|forty(?:-five)?) (?:minutes|seconds)) (?:left|to (?:spare|go|get|finish|make|pack|catch))\b",
                "Apparently {amount} now counts as a generous schedule."),
            new Aside(@"\b(?:what(?:'s| is) plan b|(?:we |i )?need (?:a )?plan b|time for plan b)\b",
                "A warm welcome to the understudy."),
            new Aside(@"\b(?:this|it|that) was supposed to be (?:easy|simple|quick)\b",
                "The word supposed is putting in quite a shift."),
            new Aside(@"\b(?:on the count of three|when i count to three)\b",
                "At last, a plan with a number attached."),
            new Aside(@"\b(?:try turning it off and on|turn it off and (?:back )?on again|have you tried restarting it)\b",
                "Technology's oldest bedtime story."),
            new Aside(@"\b(?:left|forgot) (?:my|our|the) umbrella\b",
                "The umbrella chose an excellent day to stay home.")
        };
        private static readonly Regex Sensitive = new Regex(
            @"\b(?:death|dead|die[ds]?|dying|killed|murder\w*|suicid\w*|grief|funeral|hospital\w*|injur\w*|cancer|abuse[ds]?|assault\w*|victim\w*|attack\w*|shoot\w*|gun|hostage\w*|war|emergency|mourning|missing child|accident|ambulance|evacuat\w*|disaster|refugee\w*|bomb|danger|bleeding|earthquake|tornado)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static string SelectAside(List<Cue> cues, int index, HashSet<Aside> used, out Aside selected)
        {
            selected = null;
            var cue = cues[index];
            // A sentence may cross a subtitle break ("The train is" / "running late").
            string prefix = index > 0 && cue.Start - cues[index - 1].End < SampleRate &&
                !Regex.IsMatch(cues[index - 1].Text, @"[.!?][""']?$") ? cues[index - 1].Text + " " : "";
            string line = (prefix + cue.Text).Replace('’', '\'');
            foreach (var beat in StoryBeats)
            {
                if (used.Contains(beat)) continue;
                var match = beat.Trigger.Match(line);
                if (!match.Success || match.Index + match.Length <= prefix.Length) continue;
                long vicinity = 20 * SampleRate;
                for (int i = index; i >= 0 && cues[i].End >= cue.Start - vicinity; i--)
                    if (Sensitive.IsMatch(cues[i].Text)) return null;
                for (int i = index + 1; i < cues.Count && cues[i].Start <= cue.End + vicinity; i++)
                    if (Sensitive.IsMatch(cues[i].Text)) return null;
                selected = beat;
                return beat.Line.Replace("{object}", match.Groups["object"].Value.ToLowerInvariant())
                    .Replace("{ride}", match.Groups["ride"].Value.ToLowerInvariant())
                    .Replace("{attempt}", match.Groups["attempt"].Value.ToLowerInvariant())
                    .Replace("{amount}", match.Groups["amount"].Value.ToLowerInvariant());
            }
            return null;
        }

        private static void SpeakTimeline(string tools, string stage, string pcm, List<Cue> cues, double duration,
            NarrationStyle style, Action<DownloadProgress> progress, CancellationToken token)
        {
#if !PORTABLE
            var format = new SpeechAudioFormatInfo(SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
            using (var voice = new SpeechSynthesizer())
#endif
            using (var output = new FileStream(pcm, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
#if PORTABLE
                var narrator = new NativeNarrator(tools, stage, token);
                Func<string, int, int, byte[]> speak = (text, rate, pitch) =>
                    TrimSilence(narrator.Speak(text, rate, pitch, token));
#else
                var english = voice.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en");
                if (english == null)
                    throw new InvalidOperationException("Install an English Windows desktop speech voice to create English audio.");
                voice.SelectVoice(english.VoiceInfo.Name);
                Func<string, int, int, byte[]> speak = (text, rate, pitch) =>
                    TrimSilence(Synthesize(voice, format, text, rate, pitch, token));
#endif
                long total = (long)Math.Round(duration * SampleRate);
                int previousPercent = -1;
                var used = new HashSet<Aside>();
                long nextAside = 0;
                for (int i = 0; i < cues.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var cue = cues[i];
                    int percent = (int)(100L * i / cues.Count);
                    if (percent != previousPercent)
                        Report(progress, "Speaking English dialogue (" + (i + 1) + " of " + cues.Count + ")…", percent);
                    previousPercent = percent;
                    // Spend a little available silence on clear speech before speeding up dense text.
                    long next = i + 1 < cues.Count ? cues[i + 1].Start : total;
                    long budget = Math.Min(cue.End + SampleRate * 3 / 2, next) - cue.Start;
                    int baseRate = style == NarrationStyle.Serious ? -1 : 0;
                    int pitch = style == NarrationStyle.Serious ? -9 : 0;
                    var speech = speak(cue.Text, baseRate, pitch);
                    if (speech.Length > budget * 2)
                    {
                        int rate = Math.Min(6, baseRate + (int)Math.Ceiling(Math.Log(speech.Length / (2.0 * budget)) / Math.Log(1.12)));
                        speech = speak(cue.Text, rate, pitch);
                    }
                    if (speech.Length > budget * 2)
                        speech = FitSpeech(tools, stage, speech, budget, token);
                    WriteSilence(output, cue.Start * 2, token);
                    output.Write(speech, 0, speech.Length);

                    long asideAt = Math.Max(cue.End, output.Position / 2) + SampleRate / 3;
                    if (style == NarrationStyle.Sarcastic && asideAt >= nextAside && next - asideAt >= 3 * SampleRate)
                    {
                        Aside beat;
                        string line = SelectAside(cues, i, used, out beat);
                        if (line == null) continue;
                        var aside = speak("Narrator's aside. " + line, 0, 9);
                        if (asideAt + aside.Length / 2 + SampleRate / 3 > next) continue;
                        WriteSilence(output, asideAt * 2, token);
                        output.Write(aside, 0, aside.Length);
                        used.Add(beat);
                        nextAside = asideAt + aside.Length / 2 + 45L * SampleRate;
                    }
                }
                WriteSilence(output, total * 2, token);
            }
        }

#if !PORTABLE
        private static byte[] Synthesize(SpeechSynthesizer voice, SpeechAudioFormatInfo format, string text, int rate,
            int pitch, CancellationToken token)
        {
            using (var audio = new MemoryStream())
            using (var done = new ManualResetEvent(false))
            {
                Exception error = null;
                EventHandler<SpeakCompletedEventArgs> completed = (s, e) => { error = e.Error; done.Set(); };
                voice.Rate = rate;
                voice.SetOutputToAudioStream(audio, format);
                voice.SpeakCompleted += completed;
                try
                {
                    if (pitch == 0) voice.SpeakAsync(text);
                    else
                    {
                        string pitchValue = pitch.ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%";
                        string prompt = "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"" +
                            voice.Voice.Culture.Name + "\"><prosody pitch=\"" + pitchValue + "\">" +
                            SecurityElement.Escape(text) + "</prosody></speak>";
                        voice.SpeakSsmlAsync(prompt);
                    }
                    if (WaitHandle.WaitAny(new[] { done, token.WaitHandle }) == 1)
                    {
                        voice.SpeakAsyncCancelAll();
                        done.WaitOne();
                    }
                    token.ThrowIfCancellationRequested();
                    if (error != null) throw new InvalidOperationException("The Windows English voice could not finish.", error);
                }
                finally { voice.SpeakCompleted -= completed; voice.SetOutputToNull(); }
                return audio.ToArray();
            }
        }
#endif

        private static byte[] TrimSilence(byte[] bytes)
        {
            // A voice's built-in sentence pause should not accelerate an otherwise naturally timed line.
            int start = 0, end = bytes.Length;
            while (start + 1 < end && bytes[start] == 0 && bytes[start + 1] == 0) start += 2;
            if (start >= end) throw new InvalidOperationException("The English voice produced no speech.");
            while (end > start + 2 && bytes[end - 2] == 0 && bytes[end - 1] == 0) end -= 2;
            int padding = 2 * (SampleRate / 80);
            start = Math.Max(0, start - padding);
            end = Math.Min(bytes.Length, end + padding);
            if (start == 0 && end == bytes.Length) return bytes;
            var trimmed = new byte[end - start];
            Buffer.BlockCopy(bytes, start, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        private static byte[] FitSpeech(string tools, string stage, byte[] speech, long samples, CancellationToken token)
        {
            string input = Path.Combine(stage, "fast-input.pcm");
            string output = Path.Combine(stage, "fast-output.pcm");
            File.WriteAllBytes(input, speech);
            // atempo preserves pitch. Cascaded stages stay in its high-quality range.
            double factor = 1.06 * speech.Length / (2.0 * samples);
            var filters = new List<string>();
            while (factor > 2) { filters.Add("atempo=2"); factor /= 2; }
            filters.Add("atempo=" + factor.ToString("0.000000", CultureInfo.InvariantCulture));
            var result = CaptionWorker.RunProcess(PlatformTools.Find(tools, "ffmpeg"), new[] {
                "-nostdin", "-loglevel", "error", "-y", "-f", "s16le", "-ar", "22050", "-ac", "1", "-i", input,
                "-af", string.Join(",", filters), "-f", "s16le", output
            }, stage, token, null);
            if (result.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException("An English voice segment could not be fit to its subtitle timing.");
            var bytes = File.ReadAllBytes(output);
            if (bytes.Length == 0 || bytes.Length > samples * 2)
                throw new InvalidDataException("An English subtitle is too dense for its timing. Check its start and end times, then retry.");
            return bytes;
        }

        private static void WriteSilence(Stream output, long until, CancellationToken token)
        {
            while (output.Position < until)
            {
                token.ThrowIfCancellationRequested();
                output.Write(Silence, 0, (int)Math.Min(Silence.Length, until - output.Position));
            }
        }

        private static void Report(Action<DownloadProgress> progress, string message, double? percent)
        {
            if (progress != null) progress(new DownloadProgress { Message = message, Percent = percent });
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
