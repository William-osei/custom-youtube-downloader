using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
#if !PORTABLE
using System.Windows.Forms;
#endif

namespace CustomYouTubeDownloader
{
    internal sealed class WatchlistEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Year { get; set; }
        public string Type { get; set; }
        public string Source { get; set; }
        public string Url { get; set; }
        public string SavedOn { get; set; }
    }

    internal sealed class WatchlistStore
    {
        private const int MaxEntries = 500;
        private const int MaxJsonChars = 2 * 1024 * 1024;
        private readonly string path;

        public WatchlistStore(string directory)
        {
            path = Path.Combine(directory, "watchlist.json");
        }

        public List<WatchlistEntry> Load()
        {
            return InLock(() => Read());
        }

        public bool Add(CatalogMatch match)
        {
            if (match == null || String.IsNullOrWhiteSpace(match.Title) || !IsPageLink(match.Url))
                throw new ArgumentException("Select a result with a link to save.");
            return InLock(() =>
            {
                var items = Read();
                if (items.Any(item => String.Equals(item.Url, match.Url, StringComparison.Ordinal)))
                    return false;
                if (items.Count >= MaxEntries)
                    throw new InvalidOperationException("The watchlist is full. Remove a title first.");
                items.Add(new WatchlistEntry {
                    Id = Guid.NewGuid().ToString("N"), Title = ShortText(match.Title, 220),
                    Year = ShortText(match.Year, 4), Type = ShortText(match.Type, 40),
                    Source = ShortText(match.Source, 100), Url = match.Url, SavedOn = DateTime.Now.ToString("yyyy-MM-dd")
                });
                Write(items);
                return true;
            });
        }

        public void Remove(string id)
        {
            InLock(() =>
            {
                var items = Read();
                if (items.RemoveAll(item => item.Id == id) > 0)
                    Write(items);
                return true;
            });
        }

        private List<WatchlistEntry> Read()
        {
            List<WatchlistEntry> items;
            if (TryRead(path, out items) || TryRead(path + ".bak", out items))
                return items;
            if (File.Exists(path) || File.Exists(path + ".bak"))
                throw new InvalidDataException("The saved watchlist file couldn’t be read.");
            return new List<WatchlistEntry>();
        }

        private static bool TryRead(string file, out List<WatchlistEntry> items)
        {
            items = null;
            if (!File.Exists(file))
                return false;
            try
            {
                if (new FileInfo(file).Length > MaxJsonChars * 4L)
                    return false;
                var read = CreateSerializer().Deserialize<List<WatchlistEntry>>(File.ReadAllText(file, Encoding.UTF8));
                if (read == null || read.Count > MaxEntries ||
                    read.Any(item => item == null || String.IsNullOrWhiteSpace(item.Id) ||
                        String.IsNullOrWhiteSpace(item.Title) || !IsPageLink(item.Url)))
                    return false;
                items = read;
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private void Write(List<WatchlistEntry> items)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, CreateSerializer().Serialize(items), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    List<WatchlistEntry> current;
                    string backup = TryRead(path, out current) ? path + ".bak" : null;
                    File.Replace(temporary, path, backup);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static T InLock<T>(Func<T> action)
        {
            string name = Path.DirectorySeparatorChar == '/' ? "CustomYouTubeDownloaderWatchlist-" +
                Environment.UserName.Replace('/', '_') : @"Local\CustomYouTubeDownloaderWatchlist";
            using (var mutex = new Mutex(false, name))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                        throw new IOException("The watchlist is busy. Try again.");
                    return action();
                }
                finally
                {
                    if (acquired)
                        mutex.ReleaseMutex();
                }
            }
        }

        internal static bool IsPageLink(string address)
        {
            Uri uri;
            return !String.IsNullOrWhiteSpace(address) && address.Length <= 3000 &&
                Uri.TryCreate(address, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
                !uri.IsLoopback && uri.IsDefaultPort && String.IsNullOrEmpty(uri.UserInfo);
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = MaxJsonChars, RecursionLimit = 10 };
        }

        private static string ShortText(string value, int limit)
        {
            value = (value ?? "").Trim();
            return value.Length <= limit ? value : value.Substring(0, limit);
        }
    }

#if !PORTABLE
    internal sealed class WatchlistPanel : UserControl
    {
        private readonly WatchlistStore store;
        private readonly Action<WatchlistEntry> find;
        private readonly ListView list = new ListView();
        private readonly Label info = DownloaderForm.CreateLabel("", StudioTheme.Muted);
        private readonly CollectionView collection;
        private readonly Action browse;
        private readonly Button open = DownloaderForm.CreateButton("Open saved link", true);
        private readonly Button search = DownloaderForm.CreateButton("Find this title", false);
        private readonly Button remove = DownloaderForm.CreateButton("Remove", false);

        internal WatchlistPanel(WatchlistStore store, Action<WatchlistEntry> find, Action browse = null)
        {
            this.store = store;
            this.find = find;
            this.browse = browse;
            Dock = DockStyle.Fill;
            BackColor = StudioTheme.Canvas;
            var layout = DownloaderForm.Grid(1, new Padding(0));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowCount = layout.RowStyles.Count;
            list.AccessibleName = "Saved movie and series titles";
            list.Columns.Add("Title", 380);
            list.Columns.Add("Year", 62);
            list.Columns.Add("Type", 110);
            list.Columns.Add("Source", 135);
            list.Columns.Add("Saved", 103);
            collection = new CollectionView(list, CollectionKind.Watchlist);
            list.SelectedIndexChanged += delegate { UpdateButtons(); };
            list.ItemActivate += delegate { open.PerformClick(); };
            open.Click += delegate { OpenSelected(); };
            search.Click += delegate { if (Selected() != null) find(Selected()); };
            remove.Click += delegate { RemoveSelected(); };
            layout.Controls.Add(collection, 0, 0);
            layout.Controls.Add(info, 0, 1);
            layout.Controls.Add(CollectionView.RowActions(open, search, remove), 0, 2);
            Controls.Add(layout);
            Reload();
        }

        internal string Add(CatalogMatch match)
        {
            try
            {
                bool added = store.Add(match);
                Reload();
                return added ? "Saved to your watchlist." : "This result is already on your watchlist.";
            }
            catch (Exception ex)
            {
                if (!IsStorageError(ex))
                    throw;
                return "Couldn’t save to the watchlist. " + ex.Message;
            }
        }

        internal void Reload()
        {
            var selected = Selected();
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                var entries = store.Load();
                foreach (var entry in entries.OrderByDescending(item => item.SavedOn).ThenBy(item => item.Title))
                {
                    var row = new ListViewItem(new[] { entry.Title, entry.Year ?? "", entry.Type ?? "", entry.Source ?? "", entry.SavedOn ?? "" });
                    row.Tag = entry;
                    collection.AddRow(row);
                    if (selected != null && selected.Id == entry.Id)
                        row.Selected = true;
                }
                if (entries.Count == 0)
                    collection.ShowMessage("Your watchlist is empty", "Save a movie or series from the finder to keep it here.",
                        "Find a title", browse);
                else
                    collection.ShowRows();
                info.Text = entries.Count == 0 ? "" :
                    entries.Count + (entries.Count == 1 ? " saved title" : " saved titles");
            }
            catch (Exception ex)
            {
                if (!IsStorageError(ex))
                    throw;
                collection.ShowMessage("Watchlist unavailable", "The saved titles could not be opened.", "Try again", Reload);
                info.Text = ex.Message;
            }
            finally { list.EndUpdate(); }
            UpdateButtons();
        }

        private void OpenSelected()
        {
            var item = Selected();
            if (item == null || !WatchlistStore.IsPageLink(item.Url))
                return;
            try { Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true }); }
            catch (Win32Exception) { info.Text = "Windows couldn’t open that link in your browser."; }
        }

        private void RemoveSelected()
        {
            var item = Selected();
            if (item == null)
                return;
            try
            {
                store.Remove(item.Id);
                Reload();
            }
            catch (Exception ex)
            {
                if (!IsStorageError(ex))
                    throw;
                info.Text = "Couldn’t remove this title. " + ex.Message;
            }
        }

        private WatchlistEntry Selected()
        {
            return list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as WatchlistEntry : null;
        }

        private void UpdateButtons()
        {
            open.Parent.Visible = list.Items.Count != 0;
            open.Enabled = search.Enabled = remove.Enabled = Selected() != null;
        }

        private static bool IsStorageError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException;
        }
    }
#endif
}
