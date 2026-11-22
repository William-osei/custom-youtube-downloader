using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CustomYouTubeDownloader;

// Exercised on each target OS using that platform's actual media and speech binaries.
internal static class PortableChecks
{
    internal static async Task<int> RunAsync()
    {
        var scratch = Directory.CreateTempSubdirectory("cyd-media-test-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            string tools = PlatformTools.ToolsDirectory;
            string ffmpeg = PlatformTools.Find(tools, "ffmpeg") ?? throw new IOException("FFmpeg is required for the media check.");
            string root = scratch.FullName;
            string url = DownloadWorker.NormalizeVideoUrl("https://youtu.be/abcdefghijk");
            if (!PlaylistWorker.TryNormalizePlaylistUrl("https://www.youtube.com/playlist?list=PL1234567890123456", out _))
                throw new InvalidDataException("Playlist URL handling failed.");
            var folder = await VideoLibrary.ChooseDestinationAsync(tools, url, root, "Test library", "Series 1", null, timeout.Token);
            Directory.CreateDirectory(folder.DirectoryPath);
            string original = Path.Combine(folder.DirectoryPath, "Episode 1.mkv");
            var made = CaptionWorker.RunProcess(ffmpeg, new[] { "-nostdin", "-v", "error", "-f", "lavfi",
                "-i", "color=s=160x90:r=10:d=4", "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
                "-c:v", "mpeg4", "-c:a", "aac", "-metadata:s:a:0", "language=spa", original }, root, timeout.Token, null);
            if (made.ExitCode != 0) throw new IOException("Could not write the test video. " + made.Readout);
            string sub = Path.ChangeExtension(original, ".en.srt");
            File.WriteAllText(sub, "1\n00:00:00,300 --> 00:00:03,000\nMeet me at gate four.\n");
            var transcript = EnglishTranscript.Open(sub);
            transcript.Lines[0].Text = "Meet me at gate fourteen.";
            string backup = transcript.Save();
            if (!File.Exists(backup) || !File.ReadAllText(backup).Contains("gate four."))
                throw new InvalidDataException("The prior transcript was not preserved.");
            if (EnglishTranscript.PreviewAudio(tools, original, 0.3, 1.0, timeout.Token).Length < 1000)
                throw new InvalidDataException("The original source audio could not be read.");
            string dubbed = await EnglishDubWorker.RunAsync(tools, null, original, NarrationStyle.Serious, null, timeout.Token);
            var listed = VideoLibrary.Scan(root, "Episode", timeout.Token);
            if (!listed.Any(v => v.FilePath == original) || !listed.Any(v => v.FilePath == dubbed))
                throw new InvalidDataException("The original or dubbed copy was missing from the library.");
            var history = new DownloadHistoryStore(Path.Combine(root, "profile"));
            history.Upsert(new DownloadHistoryEntry { Id = Guid.NewGuid().ToString("N"), Url = url, Title = "Episode 1",
                Root = root, Destination = folder.DirectoryPath, FilePath = dubbed, StartedUtc = DateTime.UtcNow,
                State = HistoryState.Completed, Captions = CaptionMode.English, EnglishAudio = true, AudioStyle = NarrationStyle.Serious });
            if (history.Load().Single().FilePath != dubbed) throw new InvalidDataException("History round-trip failed.");
            Console.WriteLine("Verified library, history, corrected captions, source preview, and a real dubbed video.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { scratch.Delete(true); }
    }
}
