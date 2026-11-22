using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CustomYouTubeDownloader
{
    internal sealed class NativeNarrator
    {
        private readonly string ffmpeg, synthesizer, stage, voice;
        private readonly bool mac = OperatingSystem.IsMacOS();

        internal NativeNarrator(string tools, string stage, CancellationToken token)
        {
            this.stage = stage;
            ffmpeg = PlatformTools.Find(tools, "ffmpeg");
            synthesizer = PlatformTools.Find(tools, mac ? "say" : "espeak-ng");
            if (synthesizer == null)
                throw new InvalidOperationException(mac ? "The macOS speech command 'say' is missing." :
                    "Install espeak-ng to create English narration.");
            if (mac)
            {
                var voices = CaptionWorker.RunProcess(synthesizer, new[] { "-v", "?" }, stage, token, null);
                var names = Regex.Matches(voices.Readout, @"(?m)^(?<name>[^\r\n]+?)[ \t]+en[_-]\w+[ \t]+#")
                    .Select(match => match.Groups["name"].Value.Trim()).ToArray();
                if (voices.ExitCode != 0 || names.Length == 0)
                    throw new InvalidOperationException("Add an English system voice in macOS Accessibility settings to create English narration.");
                string[] preferred = { "Samantha", "Alex", "Daniel", "Karen", "Moira" };
                voice = preferred.FirstOrDefault(wanted => names.Contains(wanted, StringComparer.Ordinal)) ?? names[0];
            }
        }

        internal byte[] Speak(string text, int rate, int pitch, CancellationToken token)
        {
            string script = Path.Combine(stage, "utterance.txt");
            string audio = Path.Combine(stage, mac ? "utterance.aiff" : "utterance.wav");
            string pcm = Path.Combine(stage, "utterance.pcm");
            try
            {
                // Read dialogue as a text file: leading dashes and long lines remain speech data.
                // Both system synthesizers recognize doubled brackets as inline control sequences.
                File.WriteAllText(script, text.Replace("[[", "[ [").Replace("]]", "] ]"), new UTF8Encoding(false));
                string wordsPerMinute = Math.Round(170 * Math.Pow(1.12, rate)).ToString(CultureInfo.InvariantCulture);
                var arguments = mac ? new[] { "-v", voice, "-r", wordsPerMinute, "-o", audio, "-f", script } :
                    new[] { "-v", "en", "-s", wordsPerMinute, "-p", (50 + pitch).ToString(CultureInfo.InvariantCulture),
                        "-b", "1", "-w", audio, "-f", script };
                var spoken = CaptionWorker.RunProcess(synthesizer, arguments, stage, token, null);
                if (spoken.ExitCode != 0 || !File.Exists(audio))
                    throw new InvalidOperationException("The English system voice could not render this subtitle.");
                var convert = new List<string> { "-nostdin", "-v", "error", "-y", "-i", audio };
                if (mac && pitch != 0)
                {
                    double ratio = 1.0 + pitch / 100.0;
                    convert.AddRange(new[] { "-af", string.Format(CultureInfo.InvariantCulture,
                        "aresample=22050,asetrate={0},aresample=22050,atempo={1:0.######}", Math.Round(22050 * ratio), 1.0 / ratio) });
                }
                convert.AddRange(new[] { "-ac", "1", "-ar", "22050", "-f", "s16le", pcm });
                if (CaptionWorker.RunProcess(ffmpeg, convert, stage, token, null).ExitCode != 0 || !File.Exists(pcm))
                    throw new InvalidDataException("The synthesized voice could not be converted into a narration track.");
                return File.ReadAllBytes(pcm);
            }
            finally
            {
                foreach (string temporary in new[] { pcm, audio, script })
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
