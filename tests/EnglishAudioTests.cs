using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// Runs on Windows with the real local FFmpeg tools and an installed English speech voice.
internal static class EnglishAudioTests
{
    private static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.LoadFrom(args[0]);
            var worker = assembly.GetType("CustomYouTubeDownloader.EnglishDubWorker", true)
                .GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic);
            var style = assembly.GetType("CustomYouTubeDownloader.NarrationStyle", true);
            string tools = args[1];
            string directory = Directory.CreateDirectory(args[2]).FullName;
            string ffmpeg = Path.Combine(tools, "ffmpeg.exe");
            foreach (string ext in new[] { "mp4", "mkv" })
            {
                string file = "source." + ext;
                Run(ffmpeg, "-v error -nostdin -y -f lavfi -i color=size=160x90:rate=10:duration=5 " +
                    "-f lavfi -i sine=frequency=330:duration=5 -c:v mpeg4 -c:a aac -metadata:s:a:0 language=spa " + file, directory);
                string original = Path.Combine(directory, file);
                byte[] before = File.ReadAllBytes(original);
                File.WriteAllText(Path.Combine(directory, "source.en.srt"),
                    "1\r\n00:00:01,000 --> 00:00:03,500\r\nWelcome to the library.\r\n");
                string dubbed = Dub(worker, style, "Faithful", tools, original);
                if (!before.SequenceEqual(File.ReadAllBytes(original))) throw new Exception("Conversion changed the source.");
                string quote = "\"" + Path.GetFileName(dubbed) + "\"";
                string probe = Path.Combine(tools, "ffprobe.exe");
                string language = Run(probe, "-v error -select_streams a:0 -show_entries stream_tags=language " +
                    "-of default=nw=1:nk=1 " + quote, directory);
                if (language.Trim() != "eng") throw new Exception("The first audio track is not English.");
                foreach (var kind in new[] { "a", "v" })
                {
                    string options = " -c copy -f hash -hash sha256 -";
                    var inputHash = Run(ffmpeg, "-v error -i " + file + " -map 0:" + kind + ":0" + options, directory);
                    var outputHash = Run(ffmpeg, "-v error -i " + quote + " -map 0:" + kind +
                        (kind == "a" ? ":1" : ":0") + options, directory);
                    if (inputHash != outputHash) throw new Exception("Original stream packets differ in the English copy.");
                }
                Run(ffmpeg, "-v error -nostdin -y -i " + quote +
                    " -map 0:a:0 -ar 16000 -ac 1 -f s16le speech.pcm", directory);
                byte[] pcm = File.ReadAllBytes(Path.Combine(directory, "speech.pcm"));
                if (Peak(pcm, 0, 0.75) > 80 || Peak(pcm, 1, 3.5) < 500 || Peak(pcm, 4, 4.8) > 80)
                    throw new Exception("The English speech track does not follow the supplied subtitle timing.");
            }
            CheckNarrationStyles(worker, style, tools, directory);
            Console.WriteLine("Real MP4/MKV English speech, three narration styles, clean subtitles, revisions and timing checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetBaseException()); return 1; }
    }

    private static string Dub(MethodInfo method, Type styleType, string style, string tools, string original)
    {
        var task = (Task<string>)method.Invoke(null,
            new object[] { tools, null, original, Enum.Parse(styleType, style), null, CancellationToken.None });
        return task.GetAwaiter().GetResult();
    }

    private static byte[] Speech(string ffmpeg, string file, string directory)
    {
        Run(ffmpeg, "-v error -nostdin -y -i \"" + file +
            "\" -map 0:a:0 -ar 16000 -ac 1 -f s16le speech.pcm", directory);
        return File.ReadAllBytes(Path.Combine(directory, "speech.pcm"));
    }

    private static void CheckNarrationStyles(MethodInfo worker, Type style, string tools, string directory)
    {
        string ffmpeg = Path.Combine(tools, "ffmpeg.exe");
        string original = Path.Combine(directory, "Station.mkv");
        Run(ffmpeg, "-v error -nostdin -y -f lavfi -i color=s=160x90:r=10:d=17 " +
            "-f lavfi -i sine=frequency=330:duration=17 -c:v mpeg4 -c:a aac Station.mkv", directory);
        string srt = Path.ChangeExtension(original, ".en.srt");
        const string first = "1\r\n00:00:00,500 --> 00:00:01,500\r\nWe just missed\r\n\r\n" +
            "2\r\n00:00:01,550 --> 00:00:02,700\r\nthe train.\r\n\r\n";
        const string second = "3\r\n00:00:11,000 --> 00:00:14,000\r\n";
        const string meeting = "Meet me at platform [fourteen] at noon.\r\n";
        File.WriteAllText(srt, first + second + meeting);
        string reserved = Path.Combine(directory, "Station [Serious English dub].mkv");
        File.WriteAllText(reserved, "Unrelated existing file.");
        string faithful = Dub(worker, style, "Faithful", tools, original);
        string serious = Dub(worker, style, "Serious", tools, original);
        string sarcastic = Dub(worker, style, "Sarcastic", tools, original);
        byte[] direct = Speech(ffmpeg, faithful, directory), story = Speech(ffmpeg, serious, directory);
        byte[] witty = Speech(ffmpeg, sarcastic, directory);
        if (File.ReadAllText(reserved) != "Unrelated existing file." || !serious.EndsWith(" (2).mkv") ||
            faithful == serious || serious == sarcastic || faithful == sarcastic ||
            direct.SequenceEqual(story) || Peak(direct, 3.5, 10) > 80 || Peak(story, 3.5, 10) > 80 ||
            Peak(witty, 3.5, 10) < 500 || Peak(witty, 11, 14) < 500)
            throw new Exception("Style tracks, aside placement or following dialogue did not match the source timing.");
        string clean = Run(ffmpeg, "-v error -i \"" + sarcastic + "\" -map 0:s:0 -f srt -", directory);
        if (!clean.Contains("We just missed") || !clean.Contains("the train.") ||
            !clean.Contains("platform [fourteen]") || clean.Contains("aside"))
            throw new Exception("Commentary changed the source subtitles.");
        if (Dub(worker, style, "Sarcastic", tools, original) != sarcastic)
            throw new Exception("An identical styled render should reuse its previous output.");
        byte[] saved = File.ReadAllBytes(sarcastic);
        File.WriteAllText(srt, first + second + "There is an emergency at the hospital.\r\n");
        string revised = Dub(worker, style, "Sarcastic", tools, original);
        if (revised == sarcastic || !saved.SequenceEqual(File.ReadAllBytes(sarcastic)) ||
            Peak(Speech(ffmpeg, revised, directory), 3.5, 10) > 80)
            throw new Exception("A revised or sensitive scene kept the aside or lost the previous render.");
        if (Dub(worker, style, "Sarcastic", tools, original) != revised)
            throw new Exception("The corrected revision was not reused.");
        File.WriteAllText(srt, first.Replace("02,700", "10,900") + second + meeting);
        string crowded = Dub(worker, style, "Sarcastic", tools, original);
        if (Peak(Speech(ffmpeg, crowded, directory), 3.5, 10) > 80)
            throw new Exception("A joke occupied time reserved for a source caption.");
    }

    private static int Peak(byte[] pcm, double start, double end)
    {
        if (pcm.Length < end * 32000) throw new Exception("The decoded English audio is too short.");
        int peak = 0;
        for (int i = (int)(start * 16000) * 2; i < (int)(end * 16000) * 2; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, i)));
        return peak;
    }

    private static string Run(string tool, string args, string directory)
    {
        using (var p = Process.Start(new ProcessStartInfo(tool, args) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory, RedirectStandardOutput = true
        }))
        {
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception(Path.GetFileName(tool) + " failed with exit code " + p.ExitCode + ".");
            return output;
        }
    }
}
