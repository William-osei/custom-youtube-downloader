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
        private readonly Label searchInfo = DownloaderForm.CreateLabel("", StudioTheme.Muted);
        private readonly TextBox summary = new TextBox();
        private readonly RowStyle detailsRow = new RowStyle(SizeType.Absolute, 0);
        private readonly ListView results = new ListView();
        private readonly CollectionView collection;
        private readonly FlowLayoutPanel actions;
        private CancellationTokenSource pending;
        private string lastCompletedQuery;

        internal DiscoveryPanel(Func<string> toolsDirectory, Action<string> addYouTube,
            Func<CatalogMatch, string> saveMatch, string initialCountry)
        {
            this.toolsDirectory = toolsDirectory;
            this.addYouTube = addYouTube;
            this.saveMatch = saveMatch;
            Dock = DockStyle.Fill;
            BackColor = StudioTheme.Canvas;

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

            var layout = DownloaderForm.Grid(4, Padding.Empty);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 84));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(detailsRow);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowCount = layout.RowStyles.Count;
            layout.Controls.Add(DownloaderForm.CreateLabel("Movie or series title", StudioTheme.Ink), 0, 0);
            var typeLabel = DownloaderForm.CreateLabel("Type", StudioTheme.Ink);
            var countryLabel = DownloaderForm.CreateLabel("Country", StudioTheme.Ink);
            layout.Controls.Add(typeLabel, 1, 0);
            layout.Controls.Add(countryLabel, 2, 0);
            title.Margin = type.Margin = region.Margin = new Padding(0, 0, 10, 0);
            layout.Controls.Add(title, 0, 1);
            layout.Controls.Add(type, 1, 1);
            layout.Controls.Add(region, 2, 1);
            search.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            search.Margin = Padding.Empty;
            layout.Controls.Add(search, 3, 1);
            ConfigureResults();
            collection = new CollectionView(results, CollectionKind.Finder);
            AddWide(layout, collection, 4);
            searchInfo.AutoSize = true;
            searchInfo.Margin = new Padding(0, 5, 0, 2);
            AddWide(layout, searchInfo, 5);

            summary.AccessibleName = "Selected title description";
            summary.Dock = DockStyle.Fill;
            summary.Margin = new Padding(0, 5, 0, 4);
            summary.Multiline = true;
            summary.ReadOnly = true;
            summary.ScrollBars = ScrollBars.Vertical;
            summary.BorderStyle = BorderStyle.None;
            summary.BackColor = StudioTheme.Canvas;
            summary.ForeColor = StudioTheme.Ink;
            summary.Visible = false;
            AddWide(layout, summary, 6);
            actions = CollectionView.RowActions(open, official, add, save);
            AddWide(layout, actions, 7);
            AddWide(layout, DownloaderForm.CreateLabel("Search a streaming service or the web", StudioTheme.Ink), 8);
            AddWide(layout, FieldWithButton(service, check, 140), 9);

            bool? compactFilters = null;
            layout.ClientSizeChanged += delegate {
                bool compact = layout.ClientSize.Width < 590 * layout.DeviceDpi / 96;
                if (compactFilters == compact)
                    return;
                compactFilters = compact;
                layout.SuspendLayout();
                try
                {
                    layout.SetCellPosition(typeLabel, new TableLayoutPanelCellPosition(compact ? 0 : 1, compact ? 2 : 0));
                    layout.SetCellPosition(countryLabel, new TableLayoutPanelCellPosition(2, compact ? 2 : 0));
                    layout.SetCellPosition(type, new TableLayoutPanelCellPosition(compact ? 0 : 1, compact ? 3 : 1));
                    layout.SetCellPosition(region, new TableLayoutPanelCellPosition(2, compact ? 3 : 1));
                    layout.SetColumnSpan(title, compact ? 3 : 1);
                    layout.SetColumnSpan(type, compact ? 2 : 1);
                    layout.SetColumnSpan(region, compact ? 2 : 1);
                    layout.RowStyles[2].Height = compact ? layout.RowStyles[0].Height : 0;
                    layout.RowStyles[3].Height = compact ? layout.RowStyles[1].Height : 0;
                }
                finally { layout.ResumeLayout(true); }
            };
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
            searchInfo.TextChanged += delegate { searchInfo.Visible = searchInfo.Text.Length != 0; };
            searchInfo.Visible = false;
            collection.ShowMessage("Find a movie or series", "Enter a title above to search the catalogs.");
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
            searchInfo.Text = "";
            collection.ShowMessage("Find a movie or series", "Enter a title above to search the catalogs.");
            UpdateSelection();
        }

        private void ConfigureResults()
        {
            results.AccessibleName = "Movie and series search results";
            results.Columns.Add("Title", 370);
            results.Columns.Add("Year", 65);
            results.Columns.Add("Type", 100);
            results.Columns.Add("Source", 145);
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
            searchInfo.Text = "";
            results.Items.Clear();
            collection.ShowMessage("Searching catalogs", "Looking for “" + query + "”…");
            UpdateSelection();
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
                        collection.AddRow(row);
                    }
                }
                finally { results.EndUpdate(); }
                lastCompletedQuery = query;
                int found = response.Matches.Count;
                if (found == 0)
                    collection.ShowMessage("No matching titles", "Try a different title or search a streaming service below.");
                else
                    collection.ShowRows();
                UpdateSelection();
                searchInfo.Text = found == 0 ? "" :
                    found + (found == 1 ? " match" : " matches") + " · Select a title to see its links.";
                if (response.Warnings.Count > 0)
                {
                    SetSummary(String.Join(Environment.NewLine, response.Warnings));
                }
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed && !Disposing)
                {
                    searchInfo.Text = "";
                    collection.ShowMessage("Search cancelled", "You can search again or enter a different title.");
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                {
                    searchInfo.Text = "";
                    collection.ShowMessage("Catalogs unavailable", "Try again or search a streaming service below.");
                    SetSummary(ex.Message);
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
            actions.Visible = results.Items.Count != 0;
            SetSummary(match == null ? "" :
                (String.IsNullOrWhiteSpace(match.Summary) ? match.Source + " catalog entry." : match.Summary));
        }

        private void SetSummary(string text)
        {
            bool visible = !String.IsNullOrWhiteSpace(text);
            detailsRow.SizeType = visible ? SizeType.Percent : SizeType.Absolute;
            detailsRow.Height = visible ? 16 : 0;
            summary.Text = text;
            summary.Visible = visible;
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
