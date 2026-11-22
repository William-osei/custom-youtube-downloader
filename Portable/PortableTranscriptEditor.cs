using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace CustomYouTubeDownloader
{
    internal sealed class PortableTranscriptEditor : Window
    {
        private readonly string video, tools, srt;
        private EnglishTranscript document;
        private readonly ListBox lines = new();
        private readonly TextBox text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        private readonly ComboBox language = new() { MinWidth = 140, MaxDropDownHeight = 240 };
        private readonly Button listen = new() { Content = "Listen to original" };
        private readonly Button generate = new() { Content = "Retranslate from audio" };
        private readonly Button save = new() { Content = "Save corrections" };
        private readonly Button discard = new() { Content = "Discard and close", IsVisible = false };
        private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly string[] codes;
        private CancellationTokenSource job;
        private bool loading, discardConfirmed, retranslating, closed;

        internal static async Task ShowAsync(Window owner, string videoFile, string toolsDirectory)
        {
            var editor = new PortableTranscriptEditor(videoFile, toolsDirectory);
            await editor.ShowDialog(owner);
        }

        private PortableTranscriptEditor(string file, string toolsDirectory)
        {
            video = file;
            tools = toolsDirectory;
            srt = Path.ChangeExtension(file, ".en.srt");
            document = EnglishTranscript.Open(srt);
            Width = 750; Height = 565; MinWidth = 480; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Title = "English transcript · " + Path.GetFileNameWithoutExtension(file);
            var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new("*,Auto,100,Auto,Auto"),
                RowSpacing = 8 };
            Content = grid;
            grid.Children.Add(lines);
            var actions = new WrapPanel();
            var labels = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                Margin = new Thickness(0, 2, 8, 2) };
            labels.Children.Add(new TextBlock { Text = "Source language", VerticalAlignment = VerticalAlignment.Center });
            labels.Children.Add(language);
            actions.Children.Add(labels);
            actions.Children.Add(generate);
            actions.Children.Add(listen);
            AddRow(grid, actions, 1);
            AddRow(grid, text, 2);
            AddRow(grid, status, 3);
            var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var close = new Button { Content = "Close" };
            footer.Children.Add(discard);
            footer.Children.Add(save);
            footer.Children.Add(close);
            AddRow(grid, footer, 4);
            codes = CaptionWorker.SourceLanguageCodes.OrderBy(NameOfLanguage, StringComparer.CurrentCultureIgnoreCase).ToArray();
            language.ItemsSource = new[] { "Automatic" }.Concat(codes.Select(c => NameOfLanguage(c) + " (" + c + ")")).ToArray();
            language.SelectedIndex = 0;
            foreach (var button in new[] { listen, generate, save, discard, close }) button.Margin = new Thickness(3, 2);
            ToolTip.SetTip(generate, "Translate the original audio again and keep the previous transcript beside the video.");
            lines.SelectionChanged += (_, _) => ReadSelection();
            text.TextChanged += (_, _) => {
                int index = lines.SelectedIndex;
                if (loading || index < 0) return;
                document.Lines[index].Text = (text.Text ?? "").ReplaceLineEndings("\n");
                status.Text = document.HasChanges ? "Unsaved corrections" : "";
                UpdateActions();
            };
            listen.Click += async (_, _) => await Listen();
            save.Click += (_, _) => Save();
            generate.Click += async (_, _) => await Translate();
            close.Click += (_, _) => Close();
            discard.Click += (_, _) => { discardConfirmed = true; Close(); };
            Closing += (_, e) => {
                if (document.HasChanges && !discardConfirmed)
                {
                    e.Cancel = true;
                    status.Text = "Save your corrections or choose Discard and close.";
                    discard.IsVisible = true;
                    return;
                }
                closed = true;
                job?.Cancel();
            };
            KeyDown += (_, e) => {
                var key = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
                if (e.Key == Key.S && (e.KeyModifiers & key) != 0 && save.IsEnabled) { Save(); e.Handled = true; }
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };
            RefreshRows(0);
        }

        private static void AddRow(Grid grid, Control child, int row)
        {
            Grid.SetRow(child, row);
            grid.Children.Add(child);
        }

        private static string NameOfLanguage(string code)
        {
            if (code == "jw") return "Javanese";
            if (code == "tl") return "Tagalog";
            try { return CultureInfo.GetCultureInfo(code).EnglishName; }
            catch (CultureNotFoundException) { return code; }
        }

        private void RefreshRows(int index)
        {
            lines.ItemsSource = document.Lines.Select(l => l.Timestamp + "    " + l.Text.Replace('\n', ' ')).ToArray();
            lines.SelectedIndex = Math.Clamp(index, 0, document.Lines.Count - 1);
            ReadSelection();
        }

        private void ReadSelection()
        {
            if (!retranslating) job?.Cancel();
            loading = true;
            try { text.Text = lines.SelectedIndex < 0 ? "" : document.Lines[lines.SelectedIndex].Text; }
            finally { loading = false; }
            UpdateActions();
        }

        private void UpdateActions()
        {
            bool ready = job == null;
            listen.IsEnabled = ready && lines.SelectedIndex >= 0;
            text.IsEnabled = !retranslating && lines.SelectedIndex >= 0;
            save.IsEnabled = ready && document.HasChanges;
            language.IsEnabled = ready;
            generate.IsEnabled = retranslating || (ready && !document.HasChanges);
            generate.Content = retranslating ? "Cancel translation" : "Retranslate from audio";
        }

        private void Save()
        {
            try
            {
                document.Save();
                RefreshRows(lines.SelectedIndex);
                discard.IsVisible = false;
                status.Text = "Corrections saved. Create English audio to hear the revised words.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
            UpdateActions();
        }

        private async Task Listen()
        {
            var line = document.Lines[lines.SelectedIndex];
            using var current = new CancellationTokenSource();
            job = current;
            UpdateActions();
            try
            {
                await Task.Run(() => {
                    string player = OperatingSystem.IsMacOS() ? PlatformTools.Find(tools, "afplay") : PlatformTools.Find(tools, "ffplay");
                    if (player == null) throw new InvalidOperationException("Install FFplay with FFmpeg to listen to the original audio.");
                    var wave = EnglishTranscript.PreviewAudio(tools, video, line.Start, line.End, current.Token);
                    string file = Path.Combine(Path.GetTempPath(), "cyd-listen-" + Guid.NewGuid().ToString("N") + ".wav");
                    try
                    {
                        File.WriteAllBytes(file, wave);
                        string[] args = OperatingSystem.IsMacOS() ? new[] { file } : new[] { "-nodisp", "-autoexit", "-loglevel", "error", file };
                        var play = CaptionWorker.RunProcess(player, args, Path.GetTempPath(), current.Token, null);
                        if (play.ExitCode != 0) throw new IOException("The audio player could not play the selected line.");
                    }
                    finally { File.Delete(file); }
                }, current.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closed) status.Text = ex.Message; }
            finally { job = null; if (!closed) UpdateActions(); }
        }

        private async Task Translate()
        {
            if (job != null) { job.Cancel(); return; }
            var prompt = new Window { Title = "Retranslate from audio", Width = 410, Height = 145, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var start = new Button { Content = "Translate" };
            var back = new Button { Content = "Cancel" };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { start, back } };
            prompt.Content = new StackPanel { Margin = new Thickness(14), Spacing = 12, Children = {
                new TextBlock { Text = "Translate the original audio again? The current English transcript will be kept as a backup.",
                    TextWrapping = TextWrapping.Wrap }, buttons } };
            start.Click += (_, _) => prompt.Close(true);
            back.Click += (_, _) => prompt.Close(false);
            if (!await prompt.ShowDialog<bool>(this)) return;
            using var current = new CancellationTokenSource();
            job = current;
            retranslating = true;
            UpdateActions();
            IProgress<DownloadProgress> updates = new Progress<DownloadProgress>(p => { if (!closed && job == current) status.Text = p.Message; });
            try
            {
                var result = await CaptionWorker.RetranslateEnglishAsync(tools, video, updates.Report, current.Token,
                    language.SelectedIndex > 0 ? codes[language.SelectedIndex - 1] : null);
                if (!closed) { document = EnglishTranscript.Open(srt); RefreshRows(0); status.Text = result.Message; }
            }
            catch (OperationCanceledException) { if (!closed) status.Text = "Translation cancelled."; }
            catch (Exception ex) { if (!closed) status.Text = ex.Message; }
            finally { job = null; retranslating = false; if (!closed) UpdateActions(); }
        }
    }
}
