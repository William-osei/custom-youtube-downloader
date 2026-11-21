using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CustomYouTubeDownloader
{
    internal enum CatalogKind
    {
        All,
        Movies,
        Series
    }

    internal sealed class CatalogMatch
    {
        public string Title;
        public string Year;
        public string Type;
        public string Source;
        public string Summary;
        public string Url;
        public string OfficialUrl;
        public string YouTubeUrl;
    }

    internal sealed class CatalogSearchResult
    {
        public readonly List<CatalogMatch> Matches = new List<CatalogMatch>();
        public readonly List<string> Warnings = new List<string>();
    }

    internal static class MediaCatalog
    {
        private const int MaximumResponseChars = 2 * 1024 * 1024;
        private const int MaximumTitleLength = 160;
        private const string WatchUrl = "https://www.youtube.com/watch?v=";
        private static readonly Regex CountryPattern = new Regex(@"\A[a-zA-Z]{2}\z", RegexOptions.CultureInvariant);
        private static readonly Regex VideoIdPattern = new Regex(@"\A[A-Za-z0-9_-]{11}\z", RegexOptions.CultureInvariant);
        private static readonly Regex WikidataIdPattern = new Regex(@"\AQ[1-9][0-9]{0,14}\z", RegexOptions.CultureInvariant);
        private static readonly Regex ArchiveIdPattern = new Regex(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,254}\z", RegexOptions.CultureInvariant);
        private static readonly Regex YearPattern = new Regex(@"\b(?:18|19|20|21)[0-9]{2}\b", RegexOptions.CultureInvariant);
        private static readonly Regex TitleWordSeparator = new Regex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant);
        private static readonly Regex SeriesPattern = new Regex(
            @"\b(?:television|TV|web|streaming|anime|animated|drama) series\b|\b(?:mini[- ]?series|telenovela|television program(?:me)?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex MoviePattern = new Regex(
            @"\bfilm\b|\bmovie\b|\bmotion picture\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex OtherPattern = new Regex(
            @"\b(?:soundtrack|film festival|film series|film franchise|character|season|episode|disambiguation)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static MediaCatalog()
        {
            // Applications compiled for .NET 4.0 otherwise inherit the legacy SSL 3.0 and TLS 1.0 defaults.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        public static async Task<CatalogSearchResult> SearchAsync(string toolsDirectory, string query, CatalogKind kind,
            string countryCode, CancellationToken token)
        {
            string title = (query ?? "").Trim();
            if (title.Length == 0 || title.Length > MaximumTitleLength || title.Any(Char.IsControl))
                throw new ArgumentException("Enter a movie or series title up to 160 characters.");
            if (kind != CatalogKind.All && kind != CatalogKind.Movies && kind != CatalogKind.Series)
                throw new ArgumentOutOfRangeException("kind");
            if (String.IsNullOrEmpty(countryCode) || !CountryPattern.IsMatch(countryCode))
                throw new ArgumentException("Select a country for the storefront search.");

            token.ThrowIfCancellationRequested();
            string region = countryCode.ToUpperInvariant();
            var sources = new List<Task<CatalogSearchResult>>();
            if (kind != CatalogKind.Movies)
                sources.Add(ReadSourceAsync("TVmaze", () => SearchTvmaze(title, token), token));
            sources.Add(ReadSourceAsync("Wikidata", () => SearchWikidata(title, kind, token), token));
            if (kind != CatalogKind.Series)
            {
                sources.Add(ReadSourceAsync("Apple storefront (" + region + ")",
                    () => SearchApple(title, region, token), token));
                sources.Add(ReadSourceAsync("Internet Archive", () => SearchArchive(title, token), token));
            }
            sources.Add(ReadSourceAsync("YouTube", () => SearchYouTube(toolsDirectory, title, token), token));

            var result = new CatalogSearchResult();
            var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var catalogTitles = new HashSet<string>(StringComparer.Ordinal);
            foreach (CatalogSearchResult page in await Task.WhenAll(sources).ConfigureAwait(false))
            {
                result.Warnings.AddRange(page.Warnings);
                foreach (var match in page.Matches)
                {
                    if (!links.Add(match.Url))
                        continue;
                    // Keep primary TVmaze details when Wikidata describes the same series and year.
                    if ((match.Source == "TVmaze" || match.Source == "Wikidata") && !String.IsNullOrEmpty(match.Year) &&
                        !catalogTitles.Add(match.Type + "|" + ComparableTitle(match.Title) + "|" + match.Year))
                        continue;
                    result.Matches.Add(match);
                }
            }

            string needle = ComparableTitle(title);
            var sorted = result.Matches.OrderBy(match => TitleMatchRank(needle, ComparableTitle(match.Title))).ToArray();
            result.Matches.Clear();
            result.Matches.AddRange(sorted);
            return result;
        }

        private static string ComparableTitle(string text)
        {
            return TitleWordSeparator.Replace(text, " ").Trim().ToUpperInvariant();
        }

        private static int TitleMatchRank(string needle, string candidate)
        {
            if (needle.Length == 0 || candidate == needle)
                return 0;
            string words = " " + candidate + " ";
            if (words.Contains(" " + needle + " "))
                return 1;
            return needle.Split(' ').All(word => words.Contains(" " + word + " ")) ? 2 : 3;
        }

        private static Task<CatalogSearchResult> ReadSourceAsync(string source, Func<CatalogSearchResult> search,
            CancellationToken token)
        {
            return Task.Run(() =>
            {
                try
                {
                    var page = search();
                    token.ThrowIfCancellationRequested();
                    return page;
                }
                catch (Exception ex)
                {
                    token.ThrowIfCancellationRequested();
                    if (!(ex is WebException || ex is IOException || ex is ArgumentException ||
                        ex is InvalidOperationException || ex is Win32Exception))
                        throw;
                    var page = new CatalogSearchResult();
                    page.Warnings.Add(source + " did not respond with usable results.");
                    return page;
                }
            }, token);
        }

        private static CatalogSearchResult SearchTvmaze(string query, CancellationToken token)
        {
            var body = ReadJson("https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(query), token);
            var result = new CatalogSearchResult();
            foreach (var item in JsonRows(body).Take(8))
            {
                var show = JsonField(item, "show");
                string name = JsonText(show, "name");
                string url = HttpsLink(JsonText(show, "url"), "tvmaze.com");
                if (String.IsNullOrWhiteSpace(name) || url == null)
                    continue;
                result.Matches.Add(new CatalogMatch {
                    Title = name, Year = FindYear(JsonText(show, "premiered")), Type = "Series", Source = "TVmaze",
                    Summary = CleanDescription(JsonText(show, "summary")), Url = url,
                    OfficialUrl = HttpsLink(JsonText(show, "officialSite"), null)
                });
            }
            return result;
        }

        private static CatalogSearchResult SearchWikidata(string query, CatalogKind kind, CancellationToken token)
        {
            var body = ReadJson("https://www.wikidata.org/w/api.php?action=wbsearchentities&format=json" +
                "&language=en&uselang=en&type=item&limit=20&search=" + Uri.EscapeDataString(query), token);
            var result = new CatalogSearchResult();
            foreach (var item in JsonRows(JsonField(body, "search")))
            {
                string id = JsonText(item, "id");
                string title = JsonText(item, "label");
                string description = JsonText(item, "description");
                if (id == null || !WikidataIdPattern.IsMatch(id) || String.IsNullOrWhiteSpace(title) ||
                    String.IsNullOrWhiteSpace(description) || OtherPattern.IsMatch(description))
                    continue;
                bool series = SeriesPattern.IsMatch(description);
                bool movie = !series && MoviePattern.IsMatch(description);
                if ((!movie && !series) || (series && kind == CatalogKind.Movies) || (movie && kind == CatalogKind.Series))
                    continue;

                result.Matches.Add(new CatalogMatch {
                    Title = title, Year = FindYear(description), Type = series ? "Series" : "Movie",
                    Source = "Wikidata", Summary = description, Url = "https://www.wikidata.org/wiki/" + id
                });
                if (result.Matches.Count == 10)
                    break;
            }
            return result;
        }

        private static CatalogSearchResult SearchApple(string query, string country, CancellationToken token)
        {
            var body = ReadJson("https://itunes.apple.com/search?media=movie&entity=movie&limit=12&country=" +
                country.ToLowerInvariant() + "&term=" + Uri.EscapeDataString(query), token);
            var result = new CatalogSearchResult();
            foreach (var item in JsonRows(JsonField(body, "results")))
            {
                string name = JsonText(item, "trackName");
                string url = HttpsLink(JsonText(item, "trackViewUrl"), "apple.com");
                if (String.IsNullOrWhiteSpace(name) || url == null ||
                    !String.Equals(JsonText(item, "kind"), "feature-movie", StringComparison.Ordinal))
                    continue;
                result.Matches.Add(new CatalogMatch {
                    Title = name, Year = FindYear(JsonText(item, "releaseDate")), Type = "Movie",
                    Source = "Apple storefront (" + country + ")",
                    Summary = CleanDescription(JsonText(item, "shortDescription") ?? JsonText(item, "longDescription")),
                    Url = url, OfficialUrl = url
                });
            }
            if (result.Matches.Count == 0)
                result.Warnings.Add("No matching Apple storefront listings for " + country + ".");
            return result;
        }

        private static CatalogSearchResult SearchArchive(string query, CancellationToken token)
        {
            string phrase = query.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string filter = "mediatype:movies AND collection:feature_films AND title:\"" + phrase + "\"";
            var body = ReadJson("https://archive.org/advancedsearch.php?output=json&rows=5" +
                "&fl%5B%5D=identifier&fl%5B%5D=title&fl%5B%5D=year&fl%5B%5D=description&q=" +
                Uri.EscapeDataString(filter), token);
            var result = new CatalogSearchResult();
            foreach (var item in JsonRows(JsonField(JsonField(body, "response"), "docs")))
            {
                string name = JsonText(item, "title");
                string id = JsonText(item, "identifier");
                if (String.IsNullOrWhiteSpace(name) || id == null || !ArchiveIdPattern.IsMatch(id))
                    continue;
                string summary = CleanDescription(JsonText(item, "description"));
                result.Matches.Add(new CatalogMatch {
                    Title = name, Year = FindYear(JsonText(item, "year")), Type = "Archive listing",
                    Source = "Internet Archive", Url = "https://archive.org/details/" + Uri.EscapeDataString(id),
                    Summary = String.IsNullOrEmpty(summary) ? "Check the item page for access and rights information." : summary
                });
            }
            return result;
        }

        private static CatalogSearchResult SearchYouTube(string toolsDirectory, string query, CancellationToken token)
        {
            string engine = Path.Combine(toolsDirectory, "yt-dlp.exe");
            if (!File.Exists(engine))
                throw new InvalidOperationException("YouTube search engine is unavailable.");
            var args = new List<string> {
                "--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--skip-download", "--flat-playlist",
                "--socket-timeout", "9", "--retries", "0", "--extractor-retries", "0", "--encoding", "utf-8",
                "--color", "never", "--print", "%(.{id,title,channel,uploader})j"
            };
            string node = DownloadWorker.FindNode(toolsDirectory);
            if (node != null)
                args.AddRange(new[] { "--no-js-runtimes", "--js-runtimes", "node:" + node });
            args.AddRange(new[] { "--", "ytsearch8:" + query + " official trailer" });
            var result = new CatalogSearchResult();
            string output = RunSearchProcess(engine, toolsDirectory, args, token);
            var serializer = NewSerializer();
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var item = serializer.DeserializeObject(line);
                string id = JsonText(item, "id");
                string title = JsonText(item, "title");
                if (id == null || !VideoIdPattern.IsMatch(id) || String.IsNullOrWhiteSpace(title))
                    continue;
                string uploader = JsonText(item, "channel") ?? JsonText(item, "uploader");
                result.Matches.Add(new CatalogMatch {
                    Title = title, Type = "YouTube video", Source = "YouTube",
                    Url = WatchUrl + id, YouTubeUrl = WatchUrl + id,
                    Summary = !String.IsNullOrWhiteSpace(uploader) ? "Uploaded by " + uploader + "." : "Video from a trailer search."
                });
            }
            return result;
        }

        private static string RunSearchProcess(string engine, string workingDirectory, List<string> args, CancellationToken token)
        {
            using (var process = new Process { StartInfo = new ProcessStartInfo {
                FileName = engine, WorkingDirectory = workingDirectory, Arguments = DownloadWorker.JoinArguments(args),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8, RedirectStandardError = true
            } })
            {
                token.ThrowIfCancellationRequested();
                if (!process.Start())
                    throw new InvalidOperationException("YouTube search could not start.");
                try
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    var elapsed = Stopwatch.StartNew();
                    while (!process.WaitForExit(100) || !stdout.IsCompleted || !stderr.IsCompleted)
                    {
                        token.ThrowIfCancellationRequested();
                        if (elapsed.ElapsedMilliseconds >= 16000)
                            throw new InvalidOperationException("YouTube search timed out.");
                        if (process.HasExited)
                            Thread.Sleep(20);
                    }
                    token.ThrowIfCancellationRequested();
                    string output = stdout.GetAwaiter().GetResult();
                    if (process.ExitCode != 0 || output.Length > MaximumResponseChars)
                        throw new InvalidOperationException("YouTube search did not return usable results.");
                    return output;
                }
                finally
                {
                    if (!process.HasExited)
                        DownloadWorker.StopProcessTree(process);
                }
            }
        }

        private static object ReadJson(string url, CancellationToken token)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 9000;
            request.ReadWriteTimeout = 6000;
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Accept = "application/json";
            request.UserAgent = "CustomYouTubeDownloader/1.2 (Windows desktop movie and series title search)";
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (timeout.Token.Register(request.Abort))
            {
                timeout.CancelAfter(12000);
                token.ThrowIfCancellationRequested();
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new InvalidOperationException("The title search did not return a result page.");
                    var text = new StringBuilder();
                    char[] segment = new char[8192];
                    int size;
                    while ((size = reader.Read(segment, 0, segment.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        if (timeout.IsCancellationRequested)
                            throw new InvalidOperationException("This title search timed out.");
                        if (text.Length + size > MaximumResponseChars)
                            throw new InvalidOperationException("This source returned too much data.");
                        text.Append(segment, 0, size);
                    }
                    return NewSerializer().DeserializeObject(text.ToString());
                }
            }
        }

        private static JavaScriptSerializer NewSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = MaximumResponseChars, RecursionLimit = 64 };
        }

        private static object JsonField(object source, string key)
        {
            var values = source as IDictionary<string, object>;
            object value;
            return values != null && values.TryGetValue(key, out value) ? value : null;
        }

        private static string JsonText(object source, string key)
        {
            var value = JsonField(source, key);
            var list = value as IList;
            if (list != null && list.Count > 0)
                value = list[0];
            return value is string || value is int ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private static IEnumerable<object> JsonRows(object source)
        {
            var rows = source as IList;
            return rows == null ? Enumerable.Empty<object>() : rows.Cast<object>();
        }

        private static string CleanDescription(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";
            string clean = Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ")), @"\s+", " ").Trim();
            const int maxDescription = 850;
            return clean.Length <= maxDescription ? clean : clean.Substring(0, maxDescription - 1).TrimEnd() + "…";
        }

        private static string FindYear(string value)
        {
            return value == null ? "" : YearPattern.Match(value).Value;
        }

        private static string HttpsLink(string value, string allowedHost)
        {
            Uri address;
            if (!Uri.TryCreate(value, UriKind.Absolute, out address) || address.Scheme != Uri.UriSchemeHttps ||
                !address.IsDefaultPort || !String.IsNullOrEmpty(address.UserInfo) || address.IsLoopback ||
                address.HostNameType != UriHostNameType.Dns || !address.Host.Contains("."))
                return null;
            if (allowedHost != null && !address.Host.Equals(allowedHost, StringComparison.OrdinalIgnoreCase) &&
                !address.Host.EndsWith("." + allowedHost, StringComparison.OrdinalIgnoreCase))
                return null;
            return address.AbsoluteUri;
        }
    }
}
