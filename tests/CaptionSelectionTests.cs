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
            Console.WriteLine("Caption selection and reuse checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory))
                File.Delete(file);
            Directory.Delete(directory);
        }
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
