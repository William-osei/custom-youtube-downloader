using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class TranscriptEditorTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            var assembly = Assembly.LoadFrom(args[0]);
            var editorType = assembly.GetType("CustomYouTubeDownloader.EnglishTranscriptEditor", true);
            string dir = Directory.CreateDirectory(args[1]).FullName;
            string video = Path.Combine(dir, "Film.mkv");
            string srt = Path.ChangeExtension(video, ".en.srt");
            string initial = "7\n00:00:01,000 --> 00:00:03,123\nHe ordered four.\n\n8\n00:00:03,555 --> 00:00:05,900\nFor his sister.\n";
            File.WriteAllText(srt, initial, Encoding.UTF8);
            byte[] originalBytes = File.ReadAllBytes(srt);
            using (var form = (Form)Activator.CreateInstance(editorType, Instance, null, new object[] { video }, null))
            {
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.Show();
                Application.DoEvents();
                var list = (ListView)editorType.GetField("lines", Instance).GetValue(form);
                var words = (TextBox)editorType.GetField("words", Instance).GetValue(form);
                var save = (Button)editorType.GetField("save", Instance).GetValue(form);
                Require(list.VirtualListSize == 2 && words.Text == "He ordered four." && !save.Enabled, "Initial subtitle selection failed.");
                words.Text = "He ordered forty.";
                list.Items[1].Selected = true;
                Application.DoEvents();
                Require(words.Text == "For his sister." && save.Enabled, "Switching lines lost corrections.");
                words.Text = "For his sister," + Environment.NewLine + "Mélanie.";
                save.PerformClick();
                Require(!save.Enabled && File.ReadAllText(srt) == initial.Replace("four.", "forty.").Replace("For his sister.", "For his sister,\nMélanie."),
                    "Saving damaged the English text or timestamps.");
                Require(originalBytes.SequenceEqual(File.ReadAllBytes(Directory.GetFiles(dir, "*.bak").Single())), "The backup differs from the original.");
                using (var timer = new System.Windows.Forms.Timer { Interval = 150 })
                {
                    timer.Tick += delegate {
                        var picker = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "Retranslate from audio");
                        if (picker == null) return;
                        timer.Stop();
                        var box = picker.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<ComboBox>().Single();
                        box.SelectedIndex = box.Items.IndexOf("French (fr)");
                        ((Button)picker.AcceptButton).PerformClick();
                    };
                    timer.Start();
                    var result = new object[] { null };
                    Require((bool)editorType.GetMethod("ChooseSourceLanguage", Instance).Invoke(form, result) &&
                        (string)result[0] == "fr", "Source language choice did not reach translation.");
                }
                form.Close();
            }
            var modelType = assembly.GetType("CustomYouTubeDownloader.EnglishTranscript", true);
            var model = modelType.GetMethod("Open", Static).Invoke(null, new object[] { srt });
            var lines = (System.Collections.IList)modelType.GetField("Lines", Instance).GetValue(model);
            lines[0].GetType().GetField("Text", Instance).SetValue(lines[0], "A pending correction.");
            File.AppendAllText(srt, "\n");
            try { modelType.GetMethod("Save", Instance).Invoke(model, null); throw new Exception("An external edit was overwritten."); }
            catch (TargetInvocationException ex) { if (!(ex.InnerException is IOException)) throw; }
            if (args.Length == 3)
            {
                string tools = args[2];
                var pcm = (byte[])editorType.GetMethod("PreviewAudio", Static).Invoke(null,
                    new object[] { tools, Path.Combine(tools, "sample.mkv"), 1.0, 1.6, CancellationToken.None });
                Require(Encoding.ASCII.GetString(pcm, 0, 4) == "RIFF" && pcm.Length > 26000 && pcm.Length < 44000,
                    "Original-audio preview did not extract the selected interval.");
            }
            Console.WriteLine("Transcript review, timed edits, backups and conflict detection passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
