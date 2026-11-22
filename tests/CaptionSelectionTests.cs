using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using CustomYouTubeDownloader;

// Compile with CaptionWorker.cs, DownloadWorker.cs and System.Web.Extensions.dll.
internal static class CaptionSelectionTests
{
    private const string Cue = "1\r\n00:00:00,000 --> 00:00:01,000\r\n";
    private const string Url = "https://www.youtube.com/watch?v=abcdefghijk";

    private static int Main(string[] args)
    {
        if (args.Length != 0)
            return RunCaptionFixture(args);

        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "cyd-caption-tests-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(directory, "yt-dlp.exe"));
            File.WriteAllBytes(Path.Combine(directory, "node.exe"), new byte[0]);
            string video = Path.Combine(directory, "episode.mkv");
            File.WriteAllText(video, "video fixture");
            string french = Path.ChangeExtension(video, ".fr.srt");
            string english = Path.ChangeExtension(video, ".en.srt");
            string requests = Path.Combine(directory, "requests.txt");
            string metadata = Path.Combine(directory, "metadata.json");
            File.WriteAllText(metadata,
                "{\"language\":\"fr\",\"subtitles\":{\"fr\":{},\"en\":{}},\"automatic_captions\":{\"en-orig\":{}}}");
            var result = Captions(directory, video, CaptionMode.OriginalAndEnglish);
            Require(!result.MissingRequested && result.Count == 2 && File.Exists(french) && File.Exists(english),
                "The French creator captions and English subtitles should both be saved.");

            File.WriteAllText(french, Cue + "Custom French captions.");
            File.Delete(english);
            File.Delete(requests);
            result = Captions(directory, video, CaptionMode.OriginalAndEnglish);
            Require(!result.MissingRequested && result.Count == 2 && File.ReadAllText(requests).Trim() == "en" &&
                File.ReadAllText(french) == Cue + "Custom French captions.",
                "Reusing the original subtitles should fetch only the missing English track.");

            File.Delete(requests);
            File.WriteAllText(metadata, "{\"subtitles\":{},\"automatic_captions\":{\"fr-orig\":{}}}");
            result = Captions(directory, video, CaptionMode.Original);
            Require(!result.MissingRequested && result.Count == 1 && !File.Exists(requests),
                "An original-language auto track should identify the existing file when language is absent.");

            File.WriteAllText(metadata, "{\"language\":\"und\",\"subtitles\":{},\"automatic_captions\":{\"fr-orig\":{}}}");
            result = Captions(directory, video, CaptionMode.Original);
            Require(!result.MissingRequested && result.Count == 1 && !File.Exists(requests),
                "An undetermined audio language should use the original-track marker.");

            File.Delete(french);
            File.WriteAllText(metadata, "{\"language\":\"fr\",\"subtitles\":{},\"automatic_captions\":{\"en-orig\":{}}}");
            result = Captions(directory, video, CaptionMode.Original);
            Require(result.MissingRequested && result.Count == 0 && !File.Exists(requests),
                "A different language should not count as the requested original subtitle.");
            CheckLocalTranslation(directory);
            Console.WriteLine("Caption selection, translation source, model and transcript recovery checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            string whisper = Path.Combine(directory, "whisper");
            if (Directory.Exists(whisper))
            {
                foreach (string file in Directory.GetFiles(whisper)) File.Delete(file);
                Directory.Delete(whisper);
            }
            foreach (string file in Directory.GetFiles(directory))
                File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static void CheckLocalTranslation(string directory)
    {
        string executable = Assembly.GetExecutingAssembly().Location;
        string whisper = Directory.CreateDirectory(Path.Combine(directory, "whisper")).FullName;
        File.Copy(executable, Path.Combine(directory, "ffprobe.exe"));
        File.Copy(executable, Path.Combine(directory, "ffmpeg.exe"));
        File.Copy(executable, Path.Combine(whisper, "whisper-cli.exe"));
        string baseModel = Path.Combine(whisper, "ggml-base.bin");
        string enhancedModel = Path.Combine(whisper, "ggml-medium-q5_0.bin");
        File.WriteAllText(baseModel, "base");
        File.WriteAllText(enhancedModel, "medium");
        string video = Path.Combine(directory, "multitrack.mkv");
        string english = Path.ChangeExtension(video, ".en.srt");
        File.WriteAllText(video, "video");
        string tracks = "{\"streams\":[" +
            "{\"index\":1,\"codec_type\":\"audio\",\"tags\":{\"language\":\"eng\",\"title\":\"Director commentary\"},\"disposition\":{\"default\":1}}," +
            "{\"index\":2,\"codec_type\":\"audio\",\"tags\":{\"language\":\"fre\"},\"disposition\":{\"original\":1}}," +
            "{\"index\":3,\"codec_type\":\"subtitle\",\"codec_name\":\"subrip\",\"tags\":{\"language\":\"eng\"},\"disposition\":{\"forced\":1}}";
        File.WriteAllText(Path.Combine(directory, "streams.json"), tracks +
            ",{\"index\":4,\"codec_type\":\"subtitle\",\"codec_name\":\"mov_text\",\"tags\":{\"language\":\"eng\"}}]}");
        var result = CaptionWorker.RunAsync(directory, null, video, CaptionMode.English, null, CancellationToken.None).Result;
        Require(!result.MissingRequested && File.ReadAllText(english).Contains("Embedded track 0:4"),
            "Full English subtitles should be used ahead of forced signs or synthetic translation.");
        string previous = File.ReadAllText(english);
        result = CaptionWorker.RetranslateEnglishAsync(directory, video, null, CancellationToken.None).GetAwaiter().GetResult();
        string[] backups = Directory.GetFiles(directory, "multitrack.en.previous-*.srt");
        string args = File.ReadAllText(Path.Combine(whisper, "last-args.txt"));
        Require(result.Count == 1 && backups.Length == 1 && File.ReadAllText(backups[0]) == previous &&
            File.ReadAllText(english).Contains("Translated dialogue") && args.Contains(enhancedModel) && args.Contains("--language fr") &&
            File.ReadAllText(Path.Combine(directory, "audio-map.txt")) == "0:2",
            "Retranslation must choose original speech, give the decoder the source language, and preserve the previous text.");
        File.Delete(enhancedModel);
        bool conflict = false;
        try
        {
            CaptionWorker.RetranslateEnglishAsync(directory, video, update =>
            {
                if (update.Message.StartsWith("Translating dialogue")) File.WriteAllText(english, Cue + "External edit.");
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (IOException) { conflict = true; }
        Require(conflict && File.ReadAllText(english) == Cue + "External edit." &&
            File.ReadAllText(Path.Combine(whisper, "last-args.txt")).Contains(baseModel) &&
            Directory.GetFiles(directory, "multitrack.en.previous-*.srt").Length == 1,
            "A legacy model should work and concurrently edited captions should be preserved.");
        File.WriteAllText(enhancedModel, "medium");
        File.Delete(english);
        File.WriteAllText(Path.Combine(directory, "streams.json"), tracks + "]}");
        File.WriteAllText(Path.Combine(directory, "metadata.json"),
            "{\"language\":\"fr\",\"subtitles\":{},\"automatic_captions\":{\"en\":{},\"fr-orig\":{}}}");
        result = Captions(directory, video, CaptionMode.English);
        Require(!result.MissingRequested && File.ReadAllText(english).Contains("Translated dialogue"),
            "Prefer the quality model over a second-hand machine translation.");
        File.Delete(english);
        File.WriteAllText(Path.Combine(whisper, "fail"), "");
        result = Captions(directory, video, CaptionMode.English);
        Require(!result.MissingRequested && File.ReadAllText(english) == Cue + "en",
            "The available machine caption track should still work if offline translation fails.");
        bool failed = false;
        try { CaptionWorker.RetranslateEnglishAsync(directory, video, null, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { failed = true; }
        Require(failed && File.ReadAllText(english) == Cue + "en", "A failed update must leave the active transcript intact.");
        File.Delete(Path.Combine(whisper, "fail"));
        result = CaptionWorker.RetranslateEnglishAsync(directory, video, null, CancellationToken.None, "es").GetAwaiter().GetResult();
        Require(!result.MissingRequested && CaptionWorker.SourceLanguageCodes.Contains("es") &&
            File.ReadAllText(Path.Combine(whisper, "last-args.txt")).Contains("--language es"),
            "A language chosen by the user should override the container tag.");
    }

    private static CaptionResult Captions(string directory, string video, CaptionMode mode)
    {
        return CaptionWorker.RunAsync(directory, Url, video, mode, null, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    // The worker launches this test executable as its local yt-dlp fixture.
    private static int RunCaptionFixture(string[] args)
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        string program = Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location);
        if (program == "ffprobe") { Console.WriteLine(File.ReadAllText(Path.Combine(directory, "streams.json"))); return 0; }
        if (program == "ffmpeg")
        {
            string map = args[Array.IndexOf(args, "-map") + 1];
            if (args.Last().EndsWith(".srt")) File.WriteAllText(args.Last(), Cue + "Embedded track " + map);
            else
            {
                File.WriteAllBytes(args.Last(), new byte[128]);
                File.WriteAllText(Path.Combine(directory, "audio-map.txt"), map);
            }
            return 0;
        }
        if (program == "whisper-cli")
        {
            if (File.Exists(Path.Combine(directory, "fail"))) return 1;
            File.WriteAllText(Path.Combine(directory, "last-args.txt"), String.Join(" ", args));
            File.WriteAllText(args[Array.IndexOf(args, "--output-file") + 1] + ".srt", Cue + "Translated dialogue.");
            return 0;
        }
        if (args.Contains("--print"))
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.WriteLine(File.ReadAllText(Path.Combine(directory, "metadata.json")));
            return 0;
        }
        int index = Array.IndexOf(args, "--sub-langs");
        if (index == -1)
            return 1;
        string tags = args[index + 1];
        File.AppendAllText(Path.Combine(directory, "requests.txt"), tags + Environment.NewLine);
        foreach (string tag in tags.Split(','))
            File.WriteAllText("caption." + tag + ".srt", Cue + tag);
        return 0;
    }
}
