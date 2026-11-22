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
                var task = (Task<string>)worker.Invoke(null, new object[] { tools, null, original, null, CancellationToken.None });
                string dubbed = task.GetAwaiter().GetResult();
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
            Console.WriteLine("Real MKV and MP4 English speech, cue timing and original-stream preservation checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetBaseException()); return 1; }
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
