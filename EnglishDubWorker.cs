using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CustomYouTubeDownloader
{
    internal static class EnglishDubWorker
    {
        internal const string Suffix = " [English dub]";
        private const int SampleRate = 22050;
        private static readonly byte[] Silence = new byte[32768];
        // Translation and voice rendering share the laptop's CPU; downloads can continue independently.
        private static readonly SemaphoreSlim RenderSlot = new SemaphoreSlim(1, 1);
        private static readonly Regex Timing = new Regex(
            @"^(?<start>\d{2,}:[0-5]\d:[0-5]\d[,.]\d{3})\s*-->\s*(?<end>\d{2,}:[0-5]\d:[0-5]\d[,.]\d{3})(?:\s.*)?$");
        private static readonly Regex Markup = new Regex(@"</?[^>]+>|\{\\[^}]*\}|\[[^\]]*\]|[♪♫]");
        private static readonly Regex Whitespace = new Regex(@"\s+");

        internal static async Task<string> RunAsync(string toolsDirectory, string sourceUrl, string videoFile,
            Action<DownloadProgress> progress, CancellationToken token)
        {
            string video = Path.GetFullPath(videoFile);
            string tools = Path.GetFullPath(toolsDirectory);
            string ext = Path.GetExtension(video).ToLowerInvariant();
            if ((ext != ".mkv" && ext != ".mp4") || Path.GetFileNameWithoutExtension(video).EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Select an original MP4 or MKV video to create English audio.");
            if (!File.Exists(video))
                throw new FileNotFoundException("The original video could not be found.", video);
            if (!File.Exists(Path.Combine(tools, "ffmpeg.exe")) || !File.Exists(Path.Combine(tools, "ffprobe.exe")))
                throw new InvalidOperationException("English audio needs FFmpeg and FFprobe. Run setup-engines.ps1 in the app folder.");
            if (RenderSlot.CurrentCount == 0) Report(progress, "Waiting for the English audio renderer…", null);
            await RenderSlot.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var captions = await CaptionWorker.RunAsync(tools, sourceUrl, video, CaptionMode.English, progress, token).ConfigureAwait(false);
                if (captions.MissingRequested || captions.Count == 0)
                    throw new InvalidOperationException(captions.Message + " For translation from speech, run setup-translation.ps1 in the app folder.");
                return await Task.Run(() => Render(tools, video, ext, progress, token), token).ConfigureAwait(false);
            }
            finally { RenderSlot.Release(); }
        }

        private static string Render(string tools, string video, string ext, Action<DownloadProgress> progress, CancellationToken token)
        {
            string stem = Path.Combine(Path.GetDirectoryName(video), Path.GetFileNameWithoutExtension(video));
            string captions = stem + ".en.srt";
            string target = stem + Suffix + ext;
            Report(progress, "Checking the original video and English transcript…", null);
            // Keep the files used for speech and the fingerprint stable through publication.
            using (var input = File.OpenRead(video))
            using (var transcript = File.OpenRead(captions))
            {
                var source = Probe(tools, video, token);
                if (source.Count("video") == 0 || source.Count("audio") == 0 || source.Duration <= 0)
                    throw new InvalidDataException("The original file needs a video track, an audio track and a known duration.");
                string signature = "CYD English dub v1 " + Digest(input, token) + " " + Digest(transcript, token);
                if (File.Exists(target))
                {
                    if (IsMatchingDub(Probe(tools, target, token), source, signature))
                        return target;
                    throw new IOException("A different file already uses the English dub name. Rename that file, then retry.");
                }

                var cues = ReadCues(captions, source.Duration, token);
                string id = Guid.NewGuid().ToString("N");
                string stage = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cyd-voice-" + id)).FullName;
                // Explicit container format lets the Library ignore the partial file until File.Move commits it.
                string pending = Path.Combine(Path.GetDirectoryName(video), ".cyd-" + id + ".tmp");
                try
                {
                    string pcm = Path.Combine(stage, "voice.pcm");
                    SpeakTimeline(tools, stage, pcm, cues, source.Duration, progress, token);
                    Report(progress, "Adding the English voice and preserving the original audio…", null);
                    var args = new List<string> {
                        "-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-i", video,
                        "-f", "s16le", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "1", "-i", pcm, "-i", captions,
                        "-map", "0:v", "-map", "1:a:0", "-map", "0:a", "-map", "0:s?", "-map", "2:s:0",
                        "-map_metadata", "0", "-map_chapters", "0", "-c", "copy", "-c:a:0", "aac", "-b:a:0", "96k",
                        "-metadata", "comment=" + signature, "-metadata:s:a:0", "language=eng",
                        "-metadata:s:a:0", "title=English narration", "-metadata:s:a:0", "handler_name=English narration",
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
                    var result = CaptionWorker.RunProcess(Path.Combine(tools, "ffmpeg.exe"), args, stage, token, null);
                    if (result.ExitCode != 0 || !IsMatchingDub(Probe(tools, pending, token), source, signature))
                        throw new InvalidOperationException("The English video could not be verified. Check available disk space and retry.");
                    token.ThrowIfCancellationRequested();
                    File.Move(pending, target);
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
            var result = CaptionWorker.RunProcess(Path.Combine(tools, "ffprobe.exe"), new[] {
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
                    Whitespace.Replace(WebUtility.HtmlDecode(Markup.Replace(line, " ")), " ").Trim())
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
            return merged;
        }

        private static long SampleAt(string timestamp)
        {
            var parts = timestamp.Split(':', ',', '.');
            long milliseconds = checked(((long.Parse(parts[0], CultureInfo.InvariantCulture) * 60 +
                int.Parse(parts[1], CultureInfo.InvariantCulture)) * 60 + int.Parse(parts[2], CultureInfo.InvariantCulture)) *
                1000 + int.Parse(parts[3], CultureInfo.InvariantCulture));
            return checked((milliseconds * SampleRate + 500) / 1000);
        }

        private static void SpeakTimeline(string tools, string stage, string pcm, List<Cue> cues, double duration,
            Action<DownloadProgress> progress, CancellationToken token)
        {
            var format = new SpeechAudioFormatInfo(SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
            using (var voice = new SpeechSynthesizer())
            using (var output = new FileStream(pcm, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var english = voice.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en");
                if (english == null)
                    throw new InvalidOperationException("Install an English Windows desktop speech voice to create English audio.");
                voice.SelectVoice(english.VoiceInfo.Name);
                long total = (long)Math.Round(duration * SampleRate);
                int previousPercent = -1;
                for (int i = 0; i < cues.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var cue = cues[i];
                    int percent = (int)(100L * i / cues.Count);
                    if (percent != previousPercent)
                        Report(progress, "Speaking English dialogue (" + (i + 1) + " of " + cues.Count + ")…", percent);
                    previousPercent = percent;
                    // Allow up to 250 ms of natural pause after a subtitle, never beyond the next cue or the video end.
                    long next = i + 1 < cues.Count ? cues[i + 1].Start : total;
                    long budget = Math.Min(cue.End + SampleRate / 4, next) - cue.Start;
                    var speech = Synthesize(voice, format, cue.Text, 0, token);
                    if (speech.Length > budget * 2)
                    {
                        int rate = Math.Min(10, (int)Math.Ceiling(Math.Log(speech.Length / (2.0 * budget)) / Math.Log(1.12)));
                        speech = Synthesize(voice, format, cue.Text, rate, token);
                    }
                    if (speech.Length > budget * 2)
                        speech = FitSpeech(tools, stage, speech, budget, token);
                    WriteSilence(output, cue.Start * 2, token);
                    output.Write(speech, 0, speech.Length);
                }
                WriteSilence(output, total * 2, token);
            }
        }

        private static byte[] Synthesize(SpeechSynthesizer voice, SpeechAudioFormatInfo format, string text, int rate, CancellationToken token)
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
                    voice.SpeakAsync(text);
                    if (WaitHandle.WaitAny(new[] { done, token.WaitHandle }) == 1)
                    {
                        voice.SpeakAsyncCancelAll();
                        done.WaitOne();
                    }
                    token.ThrowIfCancellationRequested();
                    if (error != null) throw new InvalidOperationException("The Windows English voice could not finish.", error);
                }
                finally { voice.SpeakCompleted -= completed; voice.SetOutputToNull(); }
                byte[] bytes = audio.ToArray();
                // Remove leading/trailing digital silence before fitting speech, so a voice's built-in
                // sentence pause does not accelerate an otherwise naturally timed line.
                int start = 0, end = bytes.Length;
                while (start + 1 < end && bytes[start] == 0 && bytes[start + 1] == 0) start += 2;
                if (start >= end) throw new InvalidOperationException("The Windows voice produced no speech.");
                while (end > start + 2 && bytes[end - 2] == 0 && bytes[end - 1] == 0) end -= 2;
                int padding = 2 * (SampleRate / 80);
                start = Math.Max(0, start - padding);
                end = Math.Min(bytes.Length, end + padding);
                if (start == 0 && end == bytes.Length) return bytes;
                var trimmed = new byte[end - start];
                Buffer.BlockCopy(bytes, start, trimmed, 0, trimmed.Length);
                return trimmed;
            }
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
            var result = CaptionWorker.RunProcess(Path.Combine(tools, "ffmpeg.exe"), new[] {
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
