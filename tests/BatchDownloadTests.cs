using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class WindowLayoutTests
{
    private static T FormField<T>(Form form, string name)
    {
        return (T)form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
    }

    private static async Task WaitFor(Func<bool> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(message);
            await Task.Delay(50);
        }
    }

    // The real form launches this executable as yt-dlp inside the isolated test installation.
    // Release files control when each transfer completes, independent of CPU and network speed.
    private static int RunDownloadFixture(string[] args)
    {
        string tools = AppDomain.CurrentDomain.BaseDirectory;
        if (args.Contains("--flat-playlist"))
        {
            var entries = new[] { "batchtest01", "batchtest02", "batchtest03" }
                .Select(key => new { id = key, title = key }).ToArray();
            Console.WriteLine(new JavaScriptSerializer().Serialize(new { title = "Queue fixture", entries = entries }));
            return 0;
        }
        int output = Array.IndexOf(args, "--output");
        int paths = Array.IndexOf(args, "--paths");
        if (paths < 0 || output < 0) return 2;
        string id = args.Last().Substring(args.Last().Length - 11);
        string marker = Path.Combine(tools, id);
        File.AppendAllText(marker + ".starts", "started\n");
        var clock = Stopwatch.StartNew();
        while (!File.Exists(marker + ".release"))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(35)) return 3;
            Thread.Sleep(40);
        }
        if (id == "batchtest02") { Console.Error.WriteLine("Fixture transfer failed."); return 1; }
        string prefix = args[output + 1].Split('%')[0];
        string file = Path.Combine(args[paths + 1], prefix + id + ".mkv");
        if (id == "batchtest10") File.Copy(Path.Combine(tools, "sample.mkv"), file);
        else File.WriteAllText(file, id);
        File.WriteAllText(Path.ChangeExtension(file, ".en.srt"), "1\r\n00:00:00,000 --> 00:00:00,800\r\nGood morning.\r\n");
        Console.WriteLine("CYD_FILE:" + file);
        return 0;
    }

    private static Dictionary<string, object>[] QueueHistory(string profile)
    {
        return new JavaScriptSerializer().Deserialize<Dictionary<string, object>[]>(
            File.ReadAllText(Path.Combine(profile, "history.json")));
    }

    private static string State(Form form, Dictionary<string, object> item)
    {
        return Enum.GetName(form.GetType().Assembly.GetType("CustomYouTubeDownloader.HistoryState"),
            Convert.ToInt32(item["State"]));
    }

    private static async Task CheckDownloadQueue(Form form, string profile)
    {
        string tools = Directory.CreateDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools")).FullName;
        File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(tools, "yt-dlp.exe"));
        // Basic tests exercise recovery with invalid media. The optional media run supplies real engines.
        foreach (string tool in new[] { "ffmpeg.exe", "ffprobe.exe", "node.exe" })
        {
            string path = Path.Combine(tools, tool);
            if (!File.Exists(path)) File.WriteAllBytes(path, new byte[0]);
        }

        var start = FormField<Button>(form, "download");
        var input = FormField<TextBox>(form, "url");
        var limit = FormField<NumericUpDown>(form, "parallelDownloads");
        var english = FormField<CheckBox>(form, "englishAudio");
        var style = FormField<ComboBox>(form, "audioStyle");
        var tabs = Descendants(form).OfType<TabControl>().Single();
        Func<string, string> marker = id => Path.Combine(tools, id);
        Action<string> release = id => File.WriteAllText(marker(id) + ".release", "");
        Func<string, bool> started = id => File.Exists(marker(id) + ".starts");
        const string watch = "https://www.youtube.com/watch?v=";

        tabs.SelectedIndex = 0;
        FormField<TextBox>(form, "category").Text = "Group";
        FormField<TextBox>(form, "series").Text = "Series";
        FormField<ComboBox>(form, "captions").SelectedIndex = 0;
        Require(limit.Value == 3 && !english.Checked, "Unexpected defaults for a new library.");
        limit.Value = 2;
        input.Text = "https://www.youtube.com/playlist?list=PL1234567890123456" + Environment.NewLine + watch + "batchtest04";
        start.PerformClick();
        await WaitFor(() => started("batchtest01") && started("batchtest02"), "Two playlist transfers never overlapped.");
        Require(!started("batchtest03") && !started("batchtest04"), "The batch exceeded its transfer limit.");
        release("batchtest01");
        await WaitFor(() => started("batchtest03"), "The next transfer did not start when a slot opened.");
        Require(!started("batchtest04"), "The held transfer was not counted against the limit.");
        release("batchtest02"); release("batchtest03"); release("batchtest04");
        await WaitFor(() => start.Enabled, "Mixed playlist batch did not finish.");
        var rows = QueueHistory(profile);
        Require(rows.Count(row => State(form, row) == "Completed") == 3 &&
            rows.Count(row => State(form, row) == "Failed") == 1, "One failed transfer affected other entries.");
        foreach (var row in rows.Where(row => State(form, row) == "Completed"))
        {
            string path = (string)row["FilePath"];
            Require(File.Exists(path) && ((string)row["Url"]).EndsWith(File.ReadAllText(path)), "History has another video's file.");
            int position = Convert.ToInt32(row["PlaylistPosition"]);
            if (position != 0) Require(Path.GetFileName(path).StartsWith(position.ToString("D3") + " - "), "Playlist order changed.");
        }

        limit.Value = 3;
        input.Lines = new[] { "batchtest05", "batchtest06", "batchtest07", "batchtest08" }.Select(id => watch + id).ToArray();
        start.PerformClick();
        await WaitFor(() => started("batchtest05") && started("batchtest06") && started("batchtest07"), "Cancel batch never started.");
        FormField<Button>(form, "cancel").PerformClick();
        await WaitFor(() => start.Enabled, "Cancellation did not join the active transfers.");
        rows = QueueHistory(profile);
        Require(!started("batchtest08") && rows.Count(row => State(form, row) == "Cancelled") == 3 &&
            rows.All(row => State(form, row) != "Running"), "Cancelled work continued or stayed in progress.");

        english.Checked = true;
        style.SelectedIndex = 1;
        release("batchtest09");
        input.Text = watch + "batchtest09";
        start.PerformClick();
        await WaitFor(() => start.Enabled, "Failed audio conversion did not finish.");
        var pending = QueueHistory(profile).Single(row => ((string)row["Url"]).EndsWith("batchtest09"));
        string original = (string)pending["FilePath"];
        Require(State(form, pending) == "AudioPending" && File.Exists(original) &&
            Convert.ToInt32(pending["AudioStyle"]) == 1, "Audio failure lost the original video or its narration style.");
        CheckSavedStyles(form, profile, pending);
        string sample = Path.Combine(tools, "sample.mkv");
        if (File.Exists(sample))
        {
            File.Copy(sample, original, true);
            File.SetLastWriteTimeUtc(Path.ChangeExtension(original, ".en.srt"), DateTime.UtcNow);
        }
        style.SelectedIndex = 2;
        tabs.SelectedIndex = 4;
        var list = Descendants(tabs.SelectedTab).OfType<ListView>().Single();
        var pendingRow = list.Items.Cast<ListViewItem>().Single(item =>
            (string)item.Tag.GetType().GetProperty("Id").GetValue(item.Tag, null) == (string)pending["Id"]);
        pendingRow.Selected = true;
        var retry = Descendants(tabs.SelectedTab).OfType<Button>().Single(button => button.Text.StartsWith("Retry"));
        Require(retry.Enabled, "History did not enable retry for unfinished audio.");
        retry.PerformClick();
        await WaitFor(() => start.Enabled, "Audio retry did not finish.");
        Require(File.ReadAllLines(marker("batchtest09") + ".starts").Length == 1 &&
            (File.Exists(sample) || File.ReadAllText(original) == "batchtest09"), "Audio retry downloaded again or damaged the original.");

        if (File.Exists(sample))
        {
            var recovered = QueueHistory(profile).Single(row => ((string)row["Url"]).EndsWith("batchtest09"));
            Require(State(form, recovered) == "Completed" && ((string)recovered["FilePath"]).EndsWith(" [Serious English dub].mkv"),
                "Retry must use the serious style saved in History. " + recovered["Note"]);
            tabs.SelectedIndex = 0;
            input.Text = watch + "batchtest10";
            release("batchtest10");
            start.PerformClick();
            await WaitFor(() => start.Enabled, "The real English audio job did not finish.");
            var result = QueueHistory(profile).Single(row => ((string)row["Url"]).EndsWith("batchtest10"));
            string dubbed = (string)result["FilePath"];
            Require(State(form, result) == "Completed" && dubbed.EndsWith(" [Sarcastic English dub].mkv") &&
                File.Exists(dubbed), "History did not point to the playable English copy. " + result["Note"]);

            tabs.SelectedIndex = 1;
            var library = FormField<ListView>(form, "library");
            await WaitFor(() => library.Items.Cast<ListViewItem>().Any(item => item.Text == "batchtest10"),
                "The library did not index the original video.");
            var libraryStyle = FormField<ComboBox>(form, "libraryStyle");
            var createDub = FormField<Button>(form, "libraryDub");
            var editEnglish = FormField<Button>(form, "libraryEditEnglish");
            library.Items.Cast<ListViewItem>().Single(item => item.Text == "batchtest10").Selected = true;
            Require(libraryStyle.Enabled && libraryStyle.SelectedIndex == 2 && createDub.Enabled && editEnglish.Enabled,
                "The selected original should offer its English text and narration styles.");
            foreach (Control control in new Control[] { libraryStyle, createDub, editEnglish })
            {
                string issue = await ClippedControl(tabs.SelectedTab, control);
                Require(issue == null, issue);
            }
            libraryStyle.SelectedIndex = 0;
            Require(style.SelectedIndex == 0, "The Library style is out of sync with the Download tab.");
            createDub.PerformClick();
            await WaitFor(() => start.Enabled, "Rendering a new style from Library did not finish.");
            await WaitFor(() => library.Items.Cast<ListViewItem>().Any(item => item.Text == "batchtest10 [English dub]"),
                "The second narration style did not appear beside the original.");
            foreach (ListViewItem item in library.SelectedItems.Cast<ListViewItem>().ToArray()) item.Selected = false;
            library.Items.Cast<ListViewItem>().Single(item => item.Text == "batchtest10 [English dub]").Selected = true;
            Require(!createDub.Enabled && !editEnglish.Enabled && !libraryStyle.Enabled,
                "A generated dub was incorrectly offered as a source for edits or further dubbing.");
        }
    }

    private static void CheckSavedStyles(Form form, string profile, Dictionary<string, object> sample)
    {
        var app = form.GetType().Assembly;
        Type preferences = app.GetType("CustomYouTubeDownloader.AppPreferences");
        var load = preferences.GetMethod("Load", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(string) }, null);
        Func<string, int> savedStyle = directory => Convert.ToInt32(preferences.GetField("AudioStyle")
            .GetValue(load.Invoke(null, new object[] { directory })));
        Require(savedStyle(profile) == 1, "Saved preferences lost the selected narration style.");
        string legacy = Directory.CreateDirectory(Path.Combine(profile, "older-settings")).FullName;
        string settings = Path.Combine(legacy, "settings.json");
        File.WriteAllText(settings, "{\"englishAudio\":true}");
        Require(savedStyle(legacy) == 0, "Older settings should use faithful translation.");
        File.WriteAllText(settings, "{\"englishAudio\":true,\"audioStyle\":99}");
        Require(savedStyle(legacy) == 0, "Unknown styles should use faithful translation.");

        Type store = app.GetType("CustomYouTubeDownloader.DownloadHistoryStore");
        object history = Activator.CreateInstance(store, legacy);
        var entry = new Dictionary<string, object>(sample);
        entry.Remove("AudioStyle");
        foreach (bool futureVersion in new[] { false, true })
        {
            if (futureVersion) entry["AudioStyle"] = 99;
            File.WriteAllText(Path.Combine(legacy, "history.json"), new JavaScriptSerializer().Serialize(new[] { entry }));
            var entries = (System.Collections.IEnumerable)store.GetMethod("Load").Invoke(history, null);
            object record = entries.Cast<object>().Single();
            Require(Convert.ToInt32(record.GetType().GetProperty("AudioStyle").GetValue(record, null)) == 0,
                "An older or unknown history style should retry using faithful translation.");
        }
    }
}
