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
    internal sealed class EnglishTranscript
    {
        internal sealed class Line
        {
            internal string Header, Timestamp, Text, OriginalText;
            internal double Start, End;
        }

        internal readonly List<Line> Lines = new List<Line>();
        internal bool HasChanges { get { return Lines.Any(line => line.Text != line.OriginalText); } }
        private byte[] opened;
        private string path;
        private string newline;
        private const string TimeField = @"\d{2,}:[0-5]\d:[0-5]\d[,.]\d{3}";
        private static readonly Regex Cue = new Regex(@"\A(?<header>(?:[ \t]*\d+[ \t]*\n)?[ \t]*(?<start>" +
            TimeField + @")[ \t]*-->[ \t]*(?<end>" + TimeField + @")[^\n]*\n)(?<text>[\s\S]+)\z");

        internal static EnglishTranscript Open(string path)
        {
            var doc = new EnglishTranscript { path = path, opened = File.ReadAllBytes(path) };
            string source;
            using (var reader = new StreamReader(new MemoryStream(doc.opened), new UTF8Encoding(false, true), true))
                source = reader.ReadToEnd();
            doc.newline = source.Contains("\r\n") ? "\r\n" : source.Contains("\n") ? "\n" : Environment.NewLine;
            source = source.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
            foreach (string block in Regex.Split(source, @"\n[ \t]*\n(?:[ \t]*\n)*"))
            {
                var match = Cue.Match(block);
                if (!match.Success) throw new InvalidDataException("Could not read the timing or text for subtitle " +
                    (doc.Lines.Count + 1) + ". Check the English SRT before editing.");
                var line = new Line { Header = match.Groups["header"].Value, Timestamp = match.Groups["start"].Value,
                    Text = match.Groups["text"].Value, Start = Seconds(match.Groups["start"].Value),
                    End = Seconds(match.Groups["end"].Value) };
                if (line.End <= line.Start) throw new InvalidDataException("A subtitle ends before it starts.");
                line.OriginalText = line.Text;
                doc.Lines.Add(line);
            }
            return doc;
        }

        private static double Seconds(string timestamp)
        {
            var parts = timestamp.Replace(',', '.').Split(':');
            return double.Parse(parts[0], CultureInfo.InvariantCulture) * 3600 +
                int.Parse(parts[1], CultureInfo.InvariantCulture) * 60 + double.Parse(parts[2], CultureInfo.InvariantCulture);
        }

        internal string Save()
        {
            if (!HasChanges) return null;
            for (int i = 0; i < Lines.Count; i++)
                if (Lines[i].Text.Split('\n').Any(string.IsNullOrWhiteSpace))
                    throw new InvalidDataException("Enter text for subtitle " + (i + 1) + " and remove any blank lines.");
            string body = string.Join("\n\n", Lines.Select(line => line.Header + line.Text)) + "\n";
            byte[] updated = new UTF8Encoding(false).GetBytes(body.Replace("\n", newline));
            string unique = Guid.NewGuid().ToString("N");
            string temporary = Path.Combine(Path.GetDirectoryName(path), ".cyd-" + unique + ".tmp");
            string backup = path + "." + unique + ".bak";
            try
            {
                File.WriteAllBytes(temporary, updated);
                using (var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var currentBytes = new MemoryStream())
                {
                    current.CopyTo(currentBytes);
                    if (!currentBytes.ToArray().SequenceEqual(opened))
                        throw new IOException("The transcript changed on disk. Reopen the editor to see the latest version before saving.");
                    File.Replace(temporary, path, backup);
                }
                opened = updated;
                foreach (var line in Lines) line.OriginalText = line.Text;
                return backup;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static byte[] PreviewAudio(string tools, string video, double start, double end, CancellationToken token)
        {
            string ffmpeg = PlatformTools.Find(tools, "ffmpeg");
            if (ffmpeg == null) throw new FileNotFoundException("Install FFmpeg to listen to the source audio.");
            string path = Path.Combine(Path.GetTempPath(), "cyd-listen-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                var result = CaptionWorker.RunProcess(ffmpeg, new[] {
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", video, "-map", CaptionWorker.OriginalAudioMap(tools, video, token), "-t",
                    Math.Min(30, end - start + 0.25).ToString("0.###", CultureInfo.InvariantCulture),
                    "-vn", "-ac", "1", "-ar", "22050", "-c:a", "pcm_s16le", "-n", path
                }, tools, token, null);
                if (result.ExitCode != 0 || !File.Exists(path))
                    throw new InvalidDataException("Could not play this part of the original audio.");
                return File.ReadAllBytes(path);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
