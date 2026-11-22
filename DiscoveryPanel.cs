using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace CustomYouTubeDownloader
{
    internal sealed class DiscoveryPanel : UserControl
    {
        private static readonly Color Dark = Color.FromArgb(25, 37, 57);
        private static readonly Color TextColor = Color.FromArgb(240, 246, 254);
        private static readonly Color Secondary = Color.FromArgb(176, 191, 211);

        private readonly Func<string> toolsDirectory;
        private readonly Action<string> addYouTube;
        private readonly Func<CatalogMatch, string> saveMatch;
        private readonly TextBox title = DownloaderForm.CreateInput("Movie or series title");
        private readonly ComboBox type = DownloaderForm.CreateSelector("Movie or series filter");
        private readonly ComboBox region = DownloaderForm.CreateSelector("Country for storefronts and streaming guides");
        private readonly ComboBox service = DownloaderForm.CreateSelector("Where to check");
        private readonly Button search = DownloaderForm.CreateButton("Search catalogs", true);
        private readonly Button open = DownloaderForm.CreateButton("Open result", false);
        private readonly Button official = DownloaderForm.CreateButton("Official site", false);
        private readonly Button add = DownloaderForm.CreateButton("Add YouTube link", false);
        private readonly Button save = DownloaderForm.CreateButton("Save to watchlist", false);
        private readonly Button check = DownloaderForm.CreateButton("Open search", true);
        private readonly Label searchInfo = DownloaderForm.CreateLabel("Enter a title to search.", Secondary);
        private readonly TextBox summary = new TextBox();
        private readonly ListView results = new ListView();
        private CancellationTokenSource pending;
        private string lastCompletedQuery;

        internal DiscoveryPanel(Func<string> toolsDirectory, Action<string> addYouTube,
            Func<CatalogMatch, string> saveMatch, string initialCountry)
        {
            this.toolsDirectory = toolsDirectory;
            this.addYouTube = addYouTube;
            this.saveMatch = saveMatch;
            Dock = DockStyle.Fill;
            BackColor = Dark;

            title.MaxLength = 140;
            type.Items.AddRange(new object[] { "Movies & series", "Movies", "TV series" });
            region.Items.AddRange(new object[] {
                new CountryChoice("Ghana", "GH"), new CountryChoice("United States", "US"),
                new CountryChoice("United Kingdom", "GB"), new CountryChoice("Canada", "CA"),
                new CountryChoice("Nigeria", "NG"), new CountryChoice("South Africa", "ZA"),
                new CountryChoice("India", "IN"), new CountryChoice("Germany", "DE"),
                new CountryChoice("France", "FR"), new CountryChoice("Australia", "AU")
            });
            PopulateServices();
            type.SelectedIndex = region.SelectedIndex = service.SelectedIndex = 0;
            for (int i = 0; i < region.Items.Count; i++)
                if (((CountryChoice)region.Items[i]).Code == initialCountry)
                    region.SelectedIndex = i;

            var layout = DownloaderForm.Grid(2, new Padding(18, 12, 18, 7));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            foreach (int height in new[] { 22, 34, 22, 31, 24 })
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 84));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 16));
            foreach (int height in new[] { 38, 23, 35, 21 })
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowCount = layout.RowStyles.Count;

            AddWide(layout, DownloaderForm.CreateLabel("Movie or series title", TextColor), 0);
            AddWide(layout, FieldWithButton(title, search, 150), 1);
            layout.Controls.Add(DownloaderForm.CreateLabel("Type", TextColor), 0, 2);
            layout.Controls.Add(DownloaderForm.CreateLabel("Country", TextColor), 1, 2);
            type.Margin = new Padding(0, 0, 8, 0);
            layout.Controls.Add(type, 0, 3);
            layout.Controls.Add(region, 1, 3);
            AddWide(layout, searchInfo, 4);
            ConfigureResults();
            AddWide(layout, results, 5);

            summary.AccessibleName = "Selected title description";
            summary.Dock = DockStyle.Fill;
            summary.Margin = new Padding(0, 5, 0, 4);
            summary.Multiline = true;
            summary.ReadOnly = true;
            summary.ScrollBars = ScrollBars.Vertical;
            summary.BorderStyle = BorderStyle.None;
            summary.BackColor = Dark;
            summary.ForeColor = Secondary;
            summary.Text = "Select a result for more information.";
            AddWide(layout, summary, 6);
            layout.SetRowSpan(summary, 2);
            AddWide(layout, DownloaderForm.CreateActions(open, official, add, save), 8);
            AddWide(layout, DownloaderForm.CreateLabel("Check a streaming service or search the web", TextColor), 9);
            AddWide(layout, FieldWithButton(service, check, 150), 10);
            AddWide(layout, DownloaderForm.CreateLabel("Availability varies by country. Service searches open in your browser.", Secondary), 11);
            Controls.Add(layout);

            title.TextChanged += delegate {
                check.Enabled = !String.IsNullOrWhiteSpace(title.Text);
                ResetResults();
            };
            type.SelectedIndexChanged += delegate { ResetResults(); };
            region.SelectedIndexChanged += delegate { ResetResults(); };
            title.KeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.KeyCode == Keys.Enter && pending == null)
                {
                    e.SuppressKeyPress = true;
                    search.PerformClick();
                }
            };
            search.Click += SearchClicked;
            open.Click += delegate {
                var match = Selected();
                if (match != null)
                    OpenUrl(match.Url);
            };
            official.Click += delegate {
                var match = Selected();
                if (match != null)
                    OpenUrl(match.OfficialUrl);
            };
            add.Click += delegate {
                var match = Selected();
                if (match != null && !String.IsNullOrWhiteSpace(match.YouTubeUrl))
                    addYouTube(match.YouTubeUrl);
            };
            save.Click += delegate {
                var match = Selected();
                if (match != null)
                    searchInfo.Text = saveMatch(match);
            };
            check.Click += CheckService;
            check.Enabled = false;
            UpdateSelection();
        }

        internal string CountryCode
        {
            get { return ((CountryChoice)region.SelectedItem).Code; }
        }

        internal void FindTitle(string name, string matchType)
        {
            if (pending != null)
            {
                searchInfo.Text = "Finish or cancel this search before opening a saved title.";
                return;
            }
            title.Text = name;
            type.SelectedIndex = matchType == "Movie" ? 1 : matchType == "Series" ? 2 : 0;
            SearchClicked(this, EventArgs.Empty);
        }

        private void ResetResults()
        {
            if (pending != null)
                return;
            results.Items.Clear();
            lastCompletedQuery = null;
            searchInfo.Text = "Enter a title and search the catalogs.";
            UpdateSelection();
        }

        private void ConfigureResults()
        {
            results.View = View.Details;
            results.MultiSelect = false;
            results.FullRowSelect = true;
            results.HideSelection = false;
            results.BackColor = Color.FromArgb(245, 248, 253);
            results.ForeColor = Color.FromArgb(15, 23, 39);
            results.Dock = DockStyle.Fill;
            results.Margin = Padding.Empty;
            results.AccessibleName = "Movie and series search results";
            results.Columns.Add("Title", 370);
            results.Columns.Add("Year", 65);
            results.Columns.Add("Type", 100);
            results.Columns.Add("Source", 145);
            DownloadListLayout.FitTitleColumn(results);
            results.SelectedIndexChanged += delegate { UpdateSelection(); };
            results.ItemActivate += delegate { open.PerformClick(); };
        }

        private async void SearchClicked(object sender, EventArgs e)
        {
            if (pending != null)
            {
                pending.Cancel();
                search.Enabled = false;
                searchInfo.Text = "Cancelling search…";
                return;
            }

            string query = title.Text.Trim();
            if (query.Length == 0)
            {
                searchInfo.Text = "Enter a movie or series title first.";
                title.Focus();
                return;
            }

            var active = new CancellationTokenSource();
            pending = active;
            title.Enabled = type.Enabled = region.Enabled = false;
            search.Text = "Cancel search";
            searchInfo.Text = "Searching catalogs and YouTube…";
            results.Items.Clear();
            try
            {
                var country = (CountryChoice)region.SelectedItem;
                var response = await MediaCatalog.SearchAsync(toolsDirectory(), query, (CatalogKind)type.SelectedIndex, country.Code, active.Token);
                if (IsDisposed || Disposing)
                    return;
                active.Token.ThrowIfCancellationRequested();
                results.BeginUpdate();
                try
                {
                    foreach (var match in response.Matches)
                    {
                        var row = new ListViewItem(new[] { match.Title, match.Year ?? "", match.Type, match.Source });
                        row.Tag = match;
                        results.Items.Add(row);
                    }
                }
                finally { results.EndUpdate(); }
                lastCompletedQuery = query;
                int found = response.Matches.Count;
                searchInfo.Text = found == 0 ? "No catalog matches. Try the web or a service below." :
                    found + (found == 1 ? " match" : " matches") + " · Select a title to see its links.";
                if (response.Warnings.Count > 0)
                    summary.Text = String.Join(Environment.NewLine, response.Warnings);
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed && !Disposing)
                    searchInfo.Text = "Search cancelled.";
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                {
                    searchInfo.Text = "The catalogs couldn’t be searched. Try a service below.";
                    summary.Text = ex.Message;
                }
            }
            finally
            {
                pending = null;
                active.Dispose();
                if (!IsDisposed && !Disposing)
                {
                    search.Enabled = true;
                    search.Text = "Search catalogs";
                    title.Enabled = type.Enabled = region.Enabled = true;
                }
            }
        }

        private void UpdateSelection()
        {
            var match = Selected();
            open.Enabled = match != null && !String.IsNullOrWhiteSpace(match.Url);
            official.Enabled = match != null && !String.IsNullOrWhiteSpace(match.OfficialUrl) &&
                !String.Equals(match.Url, match.OfficialUrl, StringComparison.OrdinalIgnoreCase);
            add.Enabled = match != null && !String.IsNullOrWhiteSpace(match.YouTubeUrl);
            save.Enabled = match != null && WatchlistStore.IsPageLink(match.Url);
            summary.Text = match == null ? "Select a result for more information." :
                (String.IsNullOrWhiteSpace(match.Summary) ? match.Source + " catalog entry." : match.Summary);
        }

        private CatalogMatch Selected()
        {
            return results.SelectedItems.Count == 1 ? results.SelectedItems[0].Tag as CatalogMatch : null;
        }

        private void CheckService(object sender, EventArgs e)
        {
            string query = title.Text.Trim();
            if (query.Length == 0)
                return;
            var match = Selected();
            var platform = (Service)service.SelectedItem;
            if (match != null && !String.Equals(match.Type, "YouTube video", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(query, lastCompletedQuery, StringComparison.Ordinal))
            {
                query = match.Title;
                if (service.SelectedIndex == 0 && !String.IsNullOrWhiteSpace(match.Year))
                    query += " " + match.Year;
            }
            var country = (CountryChoice)region.SelectedItem;
            OpenUrl(platform.Link(query, country));
        }

        private void OpenUrl(string link)
        {
            Uri uri;
            if (!Uri.TryCreate(link, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.IsDefaultPort || !String.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback)
                return;
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                searchInfo.Text = "Windows couldn’t open that link in your browser.";
            }
        }

        private void PopulateServices()
        {
            service.Items.AddRange(new object[] {
                new Service("Web · where to watch", (q, r) => Web(q + " where to watch " + r.Name)),
                new Service("JustWatch", (q, r) => "https://www.justwatch.com/" + (r.Code == "GB" ? "uk" : r.Code.ToLowerInvariant()) + "/search?q=" + Encode(q)),
                new Service("Netflix", (q, r) => "https://www.netflix.com/search?q=" + Encode(q)),
                new Service("Prime Video", (q, r) => "https://www.primevideo.com/search?phrase=" + Encode(q)),
                new Service("Apple TV", (q, r) => "https://tv.apple.com/" + r.Code.ToLowerInvariant() + "/search?term=" + Encode(q)),
                new Service("Plex Discover", (q, r) => "https://watch.plex.tv/search?query=" + Encode(q)),
                new Service("Disney+ · web results", (q, r) => Web(q + " site:disneyplus.com")),
                new Service("HBO Max · web results", (q, r) => Web(q + " site:hbomax.com")),
                new Service("Hulu · web results", (q, r) => Web(q + " site:hulu.com")),
                new Service("Paramount+ · web results", (q, r) => Web(q + " site:paramountplus.com")),
                new Service("Tubi · web results", (q, r) => Web(q + " site:tubitv.com")),
                new Service("Crunchyroll · web results", (q, r) => Web(q + " site:crunchyroll.com")),
                new Service("YouTube", (q, r) => "https://www.youtube.com/results?search_query=" + Encode(q)),
                new Service("Internet Archive", (q, r) => "https://archive.org/search?query=" + Encode(q + " AND mediatype:movies")),
                new Service("IMDb", (q, r) => "https://www.imdb.com/find/?q=" + Encode(q) + "&s=tt")
            });
        }

        private static string Web(string text)
        {
            return "https://www.google.com/search?q=" + Encode(text);
        }

        private static string Encode(string text)
        {
            return Uri.EscapeDataString(text);
        }

        private static Control FieldWithButton(Control field, Button button, int buttonWidth)
        {
            var row = DownloaderForm.Grid(2, Padding.Empty);
            row.RowCount = 1;
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, buttonWidth));
            button.AutoSize = false;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(8, 0, 0, 5);
            row.Controls.Add(field, 0, 0);
            row.Controls.Add(button, 1, 0);
            return row;
        }

        private static void AddWide(TableLayoutPanel layout, Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, layout.ColumnCount);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && pending != null)
                pending.Cancel();
            base.Dispose(disposing);
        }

        private sealed class CountryChoice
        {
            public readonly string Name;
            public readonly string Code;
            public CountryChoice(string name, string code) { Name = name; Code = code; }
            public override string ToString() { return Name; }
        }

        private sealed class Service
        {
            public readonly string Name;
            public readonly Func<string, CountryChoice, string> Link;
            public Service(string name, Func<string, CountryChoice, string> link) { Name = name; Link = link; }
            public override string ToString() { return Name; }
        }
    }
}
