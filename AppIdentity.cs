using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows.Forms;

[assembly: TargetFramework(".NETFramework,Version=v4.8")]
[assembly: AssemblyTitle(CustomYouTubeDownloader.AppIdentity.Name)]
[assembly: AssemblyProduct(CustomYouTubeDownloader.AppIdentity.Name)]
[assembly: AssemblyDescription("Find movies and series and manage your offline video library.")]
[assembly: AssemblyVersion(CustomYouTubeDownloader.AppIdentity.Version)]
[assembly: AssemblyFileVersion(CustomYouTubeDownloader.AppIdentity.Version)]
[assembly: AssemblyInformationalVersion(CustomYouTubeDownloader.AppIdentity.Version)]

namespace CustomYouTubeDownloader
{
    internal static class AppIdentity
    {
        internal const string Name = "Custom YouTube Downloader";
        internal const string Version = "1.0.0";
        private const string ReleasesUrl = "https://github.com/William-osei/custom-youtube-downloader/releases";
        internal static readonly Icon Icon = new Icon(typeof(AppIdentity), "AppIcon.ico");

        internal static void SetWindowIcon(Form window)
        {
            window.Icon = Icon;
            Icon scaled = null;
            Action<int> selectSize = delegate(int dpi) {
                int pixels = (32 * dpi + 48) / 96;
                if (window.Icon.Width == pixels)
                    return;
                var previous = scaled;
                scaled = new Icon(Icon, pixels, pixels);
                window.Icon = scaled;
                if (previous != null)
                    previous.Dispose();
            };
            window.HandleCreated += delegate { selectSize(window.DeviceDpi); };
            window.DpiChanged += (sender, e) => selectSize(e.DeviceDpiNew);
            window.Disposed += delegate { if (scaled != null) scaled.Dispose(); };
        }

        internal static void OpenReleases(IWin32Window owner)
        {
            try
            {
                Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                MessageBox.Show(owner, "Open the releases page in your browser:\n" + ReleasesUrl,
                    Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
