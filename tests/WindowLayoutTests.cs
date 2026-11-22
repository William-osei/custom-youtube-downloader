using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: TargetFramework(".NETFramework,Version=v4.8")]

internal static partial class WindowLayoutTests
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) == "yt-dlp")
                return RunDownloadFixture(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = CreateForm(Assembly.LoadFrom(args[0]), args[1]))
            {
                form.Opacity = 0;
                form.ShowInTaskbar = false;
                Exception failure = null;
                form.Shown += async delegate
                {
                    try
                    {
                        await CheckWindow(form);
                        await CheckDownloadQueue(form, args[1]);
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { form.Close(); }
                };
                Application.Run(form);
                if (failure != null)
                {
                    Console.Error.WriteLine(failure);
                    return 1;
                }
            }
            Console.WriteLine("Window, bounded batch, cancellation and saved-original retry checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static Form CreateForm(Assembly app, string profile)
    {
        string library = Directory.CreateDirectory(Path.Combine(profile, "Videos")).FullName;
        const string prefix = "CustomYouTubeDownloader.";
        Type prefsType = app.GetType(prefix + "AppPreferences", true);
        var load = prefsType.GetMethod("Load", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(string) }, null);
        object prefs = load.Invoke(null, new object[] { profile });
        prefsType.GetField("Folder").SetValue(prefs, library);
        object watchlist = Activator.CreateInstance(app.GetType(prefix + "WatchlistStore", true), profile);
        object history = Activator.CreateInstance(app.GetType(prefix + "DownloadHistoryStore", true), profile);
        return (Form)Activator.CreateInstance(app.GetType(prefix + "DownloaderForm", true),
            BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { prefs, watchlist, history }, null);
    }

    private static async Task CheckWindow(Form form)
    {
        await Task.Yield();
        var screen = Screen.FromControl(form);
        Require(screen.WorkingArea.Contains(form.Bounds), "The initial window extends outside the working area.");
        CheckIcon(form);
        var tabs = Descendants(form).OfType<TabControl>().Single();
        Require(tabs.SelectedIndex == 0 && NavigationButton(form, 0).AccessibleDescription == "Current workspace",
            "The initial Download workspace is missing its selection indicator.");
        var linkBox = Descendants(tabs.SelectedTab).OfType<TextBox>().Single(control => control.Multiline);
        var audio = Descendants(tabs.SelectedTab).OfType<CheckBox>().Single(c => c.AccessibleName == "Add English audio");
        var style = Descendants(tabs.SelectedTab).OfType<ComboBox>().Single(c => c.AccessibleName == "English narration style");
        var concurrency = Descendants(tabs.SelectedTab).OfType<NumericUpDown>()
            .Single(c => c.AccessibleName == "Simultaneous downloads");
        Require(!audio.Checked && concurrency.Minimum == 1 && concurrency.Maximum == 4 && concurrency.Value == 3,
            "A fresh profile should use original audio and three parallel downloads.");
        Require(!style.Enabled && style.SelectedIndex == 0 && style.Items.Cast<string>().SequenceEqual(
            new[] { "Faithful translation", "Serious storyteller", "Sarcastic storyteller" }), "Unexpected narration choices.");
        audio.Checked = true;
        Require(style.Enabled && style.TabStop, "Enabling English audio did not enable keyboard style selection.");
        audio.Checked = false;

        form.Size = form.MinimumSize;
        await Task.Yield();
        var normalBounds = form.Bounds;
        var normalLinkSize = linkBox.Size;
        var page = tabs.SelectedTab;
        var compactContent = page.Controls[0].Size;
        Require(page.DisplayRectangle.Height > page.ClientSize.Height, "The compact download page does not scroll.");
        var clipped = new List<string>();
        await CheckNavigation(form, tabs);
        foreach (TabPage workspace in tabs.TabPages)
        {
            NavigationButton(form, tabs.TabPages.IndexOf(workspace)).PerformClick();
            await Task.Yield();
            Require(tabs.SelectedTab == workspace, "The sidebar did not open " + workspace.Text + ".");
            foreach (var control in Descendants(workspace).Where(control => control.Visible &&
                (control is Button || control is LinkLabel || control is ComboBox || control is CheckBox ||
                 control is NumericUpDown)))
            {
                string issue = await ClippedControl(workspace, control);
                if (issue != null) clipped.Add(issue);
            }
        }
        tabs.SelectedTab = page;
        await Task.Yield();

        Key(linkBox, Keys.F11);
        await Task.Yield();
        CheckFullScreen(form, screen);
        Key(linkBox, Keys.Escape);
        await Task.Yield();
        CheckNormal(form, normalBounds);

        form.WindowState = FormWindowState.Maximized;
        await Task.Yield();
        Require(form.WindowState == FormWindowState.Maximized, "Maximize did not take effect.");
        await CheckNavigation(form, tabs);
        if (page.Controls[0].Width > compactContent.Width)
            Require(linkBox.Width > normalLinkSize.Width, "The URL editor did not expand horizontally.");
        if (page.Controls[0].Height > compactContent.Height)
            Require(linkBox.Height > normalLinkSize.Height, "The URL editor did not expand vertically.");
        Key(linkBox, Keys.F11);
        await Task.Yield();
        CheckFullScreen(form, screen);
        Key(linkBox, Keys.F11);
        await Task.Yield();
        Require(form.WindowState == FormWindowState.Maximized && form.FormBorderStyle == FormBorderStyle.Sizable,
            "F11 did not restore the maximized window.");
        Key(linkBox, Keys.F11);
        await Task.Yield();
        Key(linkBox, Keys.Escape);
        await Task.Yield();
        Require(form.WindowState == FormWindowState.Maximized, "Escape did not restore the maximized window.");
        form.WindowState = FormWindowState.Normal;
        await Task.Yield();
        CheckNormal(form, normalBounds);
        if (clipped.Count != 0)
        {
            using (var graphics = form.CreateGraphics())
                throw new InvalidOperationException("Compact layout at " + graphics.DpiX + " DPI, " +
                    form.Font.SizeInPoints + "pt (" + normalBounds.Size + "):" + Environment.NewLine +
                    string.Join(Environment.NewLine, clipped));
        }

        var start = Descendants(page).OfType<Button>().Single(b => b.Text == "Start downloads");
        start.PerformClick();
        await Task.Yield();
        var guidance = Descendants(form).OfType<Label>().Single(l => l.Text.Contains("Paste at least one"));
        Require(guidance.Visible && form.RectangleToScreen(form.ClientRectangle)
            .Contains(guidance.RectangleToScreen(guidance.ClientRectangle)),
            "The compact workspace hides validation feedback for an empty queue.");
    }

    private static async Task<string> ClippedControl(TabPage workspace, Control control)
    {
        // A fullscreen application on the desktop can minimize the background test form.
        var window = workspace.FindForm();
        var pendingLayout = System.Diagnostics.Stopwatch.StartNew();
        while (workspace.ClientSize.Height < control.Height && pendingLayout.ElapsedMilliseconds < 2000)
        {
            if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
            await Task.Delay(20);
        }
        workspace.ScrollControlIntoView(control);
        await Task.Yield();
        var bounds = control.Parent.RectangleToScreen(control.Bounds);
        for (Control parent = control.Parent; parent != workspace.Parent; parent = parent.Parent)
        {
            var viewport = parent.RectangleToScreen(parent.ClientRectangle);
            if (!viewport.Contains(bounds))
                return workspace.Text + " clipped " + (control.AccessibleName ?? control.Text) +
                    " at " + bounds + " inside " + parent.GetType().Name + " " + viewport +
                    " (window " + window.WindowState + " " + window.Bounds + ").";
        }
        return null;
    }

    private static Button NavigationButton(Form form, int index)
    {
        return Descendants(form).OfType<Button>().Single(control => control.Name == "nav" + index);
    }

    private static async Task CheckNavigation(Form form, TabControl tabs)
    {
        var previous = tabs.SelectedTab;
        Require(tabs.TabCount == 5, "One or more workspaces are missing.");
        for (int index = 0; index < tabs.TabCount; index++)
        {
            var button = NavigationButton(form, index);
            Require(button.Visible && button.TabStop && button.CanSelect &&
                button.AccessibleName == tabs.TabPages[index].Text,
                "The keyboard or accessible name is missing for workspace " + index + ".");
            var bounds = button.RectangleToScreen(button.ClientRectangle);
            for (Control parent = button.Parent; parent != null; parent = parent.Parent)
                Require(parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds),
                    "The sidebar clips the button for " + button.AccessibleName + ".");

            tabs.SelectedIndex = (index + 1) % tabs.TabCount;
            button.Select();
            Key(button, Keys.Enter);
            await Task.Yield();
            Require(tabs.SelectedIndex == index, "Enter did not open " + button.AccessibleName + ".");
            Require(button.AccessibleDescription == "Current workspace",
                "The sidebar did not announce " + button.AccessibleName + " as the current workspace.");
            int next = (index + 1) % tabs.TabCount;
            Key(button, Keys.Control | (Keys)((int)Keys.D1 + next));
            await Task.Yield();
            Require(tabs.SelectedIndex == next, "Ctrl+" + (next + 1) + " did not open its workspace.");
        }
        tabs.SelectedIndex = tabs.TabCount - 1;
        var last = NavigationButton(form, tabs.SelectedIndex);
        last.Select();
        Key(last, Keys.Control | Keys.Tab);
        await Task.Yield();
        Require(tabs.SelectedIndex == 0, "Ctrl+Tab did not wrap from the last page.");
        Key(last, Keys.Control | Keys.Shift | Keys.Tab);
        await Task.Yield();
        Require(tabs.SelectedIndex == tabs.TabCount - 1, "Ctrl+Shift+Tab did not wrap from the first page.");
        tabs.SelectedTab = previous;
        await Task.Yield();
    }

    private static void Key(Control control, Keys key)
    {
        var message = Message.Create(control.Handle, 0x0100, (IntPtr)(int)key, IntPtr.Zero);
        Require(control.PreProcessMessage(ref message), key + " was not handled from the editor.");
    }

    private static void CheckFullScreen(Form form, Screen screen)
    {
        Require(form.FormBorderStyle == FormBorderStyle.None && form.Bounds == screen.Bounds,
            "Fullscreen bounds " + form.Bounds + " do not match display bounds " + screen.Bounds + ".");
    }

    private static void CheckNormal(Form form, Rectangle expected)
    {
        Require(form.WindowState == FormWindowState.Normal && form.FormBorderStyle == FormBorderStyle.Sizable &&
            form.Bounds == expected, "Restored bounds " + form.Bounds + " differ from " + expected + ".");
    }

    private static void CheckIcon(Form form)
    {
        var icon = form.Icon;
        using (var stream = new MemoryStream())
        using (var reader = new BinaryReader(stream))
        {
            icon.Save(stream);
            stream.Position = 0;
            Require(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1, "The window icon is invalid.");
            int count = reader.ReadUInt16();
            var sizes = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                int width = reader.ReadByte();
                int height = reader.ReadByte();
                Require(width == height, "The window icon has a nonsquare frame.");
                sizes.Add(width == 0 ? 256 : width);
                stream.Position += 14;
            }
            Require(new[] { 16, 20, 32, 40, 48, 256 }.All(sizes.Contains), "The window icon is missing expected sizes.");
            int target = (32 * form.DeviceDpi + 48) / 96;
            if (sizes.Contains(target))
                Require(icon.Width == target && icon.Height == target, "The window did not select the " +
                    target + "px icon frame at " + form.DeviceDpi + " DPI.");
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        return parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
