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

internal static class WindowLayoutTests
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = CreateForm(Assembly.LoadFrom(args[0]), args[1]))
            {
                form.Opacity = 0;
                form.ShowInTaskbar = false;
                Exception failure = null;
                form.Shown += async delegate
                {
                    try { await CheckWindow(form); }
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
            Console.WriteLine("Window sizing, fullscreen shortcuts, compact scrolling and icon checks passed.");
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
        var linkBox = Descendants(tabs.SelectedTab).OfType<TextBox>().Single(control => control.Multiline);

        form.Size = form.MinimumSize;
        await Task.Yield();
        var normalBounds = form.Bounds;
        var normalLinkSize = linkBox.Size;
        var page = tabs.SelectedTab;
        var compactContent = page.Controls[0].Size;
        Require(page.DisplayRectangle.Height > page.ClientSize.Height, "The compact download page does not scroll.");
        var clipped = new List<string>();
        foreach (TabPage workspace in tabs.TabPages)
        {
            tabs.SelectedTab = workspace;
            await Task.Yield();
            foreach (var button in Descendants(workspace).OfType<Button>().Where(button => button.Visible))
            {
                workspace.ScrollControlIntoView(button);
                await Task.Yield();
                var bounds = button.Parent.RectangleToScreen(button.Bounds);
                for (Control parent = button.Parent; parent != workspace.Parent; parent = parent.Parent)
                {
                    var viewport = parent.RectangleToScreen(parent.ClientRectangle);
                    if (!viewport.Contains(bounds))
                    {
                        clipped.Add(workspace.Text + " clipped " + button.Text + " at " + bounds + " inside " +
                            parent.GetType().Name + " " + viewport + ".");
                        break;
                    }
                }
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
