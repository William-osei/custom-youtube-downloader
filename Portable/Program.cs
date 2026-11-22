using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace CustomYouTubeDownloader;

internal static class Program
{
    internal static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "";
    internal static bool SmokeTest { get; private set; }
    internal static string Profile { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version") { Console.WriteLine(Version); return 0; }
        if (args.Length == 1 && args[0] == "--self-test") return PortableChecks.RunAsync().GetAwaiter().GetResult();
        SmokeTest = args.Length == 1 && args[0] == "--smoke-test";
        DirectoryInfo temporary = null;
        try
        {
            temporary = SmokeTest ? Directory.CreateTempSubdirectory("cyd-desktop-smoke-") : null;
            Profile = temporary?.FullName ?? PlatformTools.SettingsDirectory;
            Directory.CreateDirectory(Profile);
            using var instance = new FileStream(Path.Combine(Profile, "desktop.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return AppBuilder.Configure<DesktopApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) { Console.Error.WriteLine("Could not start Custom YouTube Downloader. " + ex.Message); return 1; }
        finally { if (temporary != null && temporary.Exists) temporary.Delete(true); }
    }
}

public sealed class DesktopApp : Application
{
    public override void Initialize()
    {
        Name = "Custom YouTube Downloader";
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new StudioWindow(Program.Profile);
            desktop.MainWindow = window;
            if (Program.SmokeTest)
            {
                window.Opened += async (_, _) => {
                    try
                    {
                        await window.ExerciseWorkspacesAsync();
                        Console.WriteLine("Opened native window and verified Download, Library, Discover, Watchlist and History.");
                        desktop.Shutdown(0);
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); desktop.Shutdown(1); }
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
