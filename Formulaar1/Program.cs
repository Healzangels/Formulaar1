using APIv3SonarrDotcore.Api;
using APIv3SonarrDotcore.Model;
using Microsoft.AspNetCore.Http.Extensions;
using Newtonsoft.Json.Linq;
using QBittorrent.Client;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using static Formulaar1.Helpers;
using Configuration = APIv3SonarrDotcore.Client.Configuration;

namespace Formulaar1
{
    public class Program
    {
        private static Bugsnag.Client? _bugsnag;

        private static SeriesApi? _seriesApi;
        private static ReleasePushApi? _releasePushApi;
        private static CommandApi? _commandApi;
        // _episodeApi and _historyApi were here previously but both moved to
        // shims (SonarrEpisodeShim, SonarrHistoryShim) so we no longer depend
        // on the abandoned APIv3SonarrDotcore deserialiser for read paths.
        private static HttpClient _httpClient = new();

        private static QBittorrentClient? _qBittorrentClient;

        // Releases accepted by Sonarr and awaiting qBit completion. Keyed by
        // Title (the rewritten one we set in the release-push handler, unique
        // per release). Switched from ConcurrentBag to ConcurrentDictionary in
        // fix15: the old "_hashes = new Bag(_hashes.Except([r]))" pattern had a
        // race where a concurrently-added release could land in the soon-to-be-
        // orphaned old bag. TryAdd/TryRemove on ConcurrentDictionary is atomic.
        private static ConcurrentDictionary<string, ReleaseResource> _hashes = new();

        // Tracks when each release was added to _hashes (keyed by Title). Used by
        // the hardlink monitor to evict stuck entries -- releases that were
        // accepted by Sonarr but never resolved to a completed qBit torrent.
        // Without this, a release whose InfoHash never gets matched (qBit drop,
        // history mismatch, etc.) sits in _hashes for the container's lifetime
        // and the monitor keeps logging diagnostics for it every 10 seconds.
        private static ConcurrentDictionary<string, DateTime> _queuedAt = new();

        // Wall-clock UTC at startup, surfaced via /health for uptime tracking.
        private static readonly DateTime _startedAt = DateTime.UtcNow;

        private static System.Timers.Timer _timer = new System.Timers.Timer();

        private static string? TorrentClient, BaseSonarPath, BaseqBitPath, SonarApiKey, qBitUsername, qBitPassword, bugsnagApiKey, Hardlinkpath;

        // `running` flag removed in fix15: Timer.AutoReset=false means the timer
        // is single-fire, the handler runs to completion, and only the handler
        // itself can reschedule via _timer.Start(). So there's never more than
        // one in-flight handler -- the manual mutex was dead weight.
        private static bool bugsnagEnabled = true;
        private static bool enableHardlinking = false;

        public static void Main(string[] args)
        {
            using IHost host = Host.CreateDefaultBuilder(args).Build();

            IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

            SonarApiKey = config.GetValue<string>("APICredentials:Sonarr:ApiKey");
            BaseSonarPath = config.GetValue<string>("APICredentials:Sonarr:BasePath");
            TorrentClient = config.GetValue<string>("TorrentClient");
            qBitUsername = config.GetValue<string>("APICredentials:qBittorrentClient:Username");
            qBitPassword = config.GetValue<string>("APICredentials:qBittorrentClient:Password");
            BaseqBitPath = config.GetValue<string>("APICredentials:qBittorrentClient:BasePath");
            // BugSnag telemetry requires BOTH top-level AllowBugSnag AND the
            // nested enabled flag. Upstream only read the nested flag and
            // ignored AllowBugSnag entirely, making the top-level toggle
            // decorative. With this AND-gate, AllowBugSnag is a kill switch
            // a user can flip to disable telemetry without touching the
            // nested config.
            bugsnagEnabled = config.GetValue<bool>("AllowBugSnag") &&
                             config.GetValue<bool>("APICredentials:bugsnag:enabled");
            bugsnagApiKey = config.GetValue<string>("APICredentials:bugsnag:apiKey");
            Hardlinkpath = config.GetValue<string>("Hardlinkpath");
            enableHardlinking = config.GetValue<bool>("EnableHardlinking");

            if (bugsnagEnabled)
            {
                _bugsnag = new Bugsnag.Client(bugsnagApiKey);
            }

            if (enableHardlinking && !Directory.Exists(Hardlinkpath))
            {
                Directory.CreateDirectory(Hardlinkpath!);
            }

            //Configuring Sonarr API
            if (BaseSonarPath != null && SonarApiKey != null)
            {

                Configuration.Default.BasePath = BaseSonarPath;
                Configuration.Default.ApiKey.Add("X-Api-Key", SonarApiKey);
                Configuration.Default.UserAgent = "Formulaar1";

                _seriesApi = new SeriesApi();
                _releasePushApi = new ReleasePushApi();
                _commandApi = new CommandApi();
                // _episodeApi and _historyApi moved to shims (SonarrEpisodeShim,
                // SonarrHistoryShim) so the bundled APIv3SonarrDotcore client is
                // only used now for release-push (ReleasePushApi) and triggering
                // scan commands (CommandApi), neither of which return MediaCover-
                // tainted payloads. Series is also shimmed via SonarrSeriesShim.
            }
            else
            {
                Console.WriteLine("#####################################################################################");
                Console.WriteLine("## !!Please check the Sonarr section is configured correctly in appsettings.json!! ##");
                Console.WriteLine("#####################################################################################");

            }

            //Attempt to confiugure download client API's.
            try
            {
                if (TorrentClient == "qBittorrent" && qBitUsername != null && qBitPassword != null)
                {
                    Console.WriteLine($"Detected qBittorrent Client, attempting to login");
                    _qBittorrentClient = new QBittorrentClient(new Uri(BaseqBitPath!));
                    _qBittorrentClient.LoginAsync(qBitUsername, qBitPassword).GetAwaiter().GetResult();
                    var result = _qBittorrentClient.GetQBittorrentVersionAsync().GetAwaiter().GetResult();
                    Console.WriteLine($"Logged in to {result}");
                }
                else
                {
                    Console.WriteLine("###########################################################################################");
                    Console.WriteLine("##  !!Please check the qBittorrent section is configured correctly in appsettings.json!! ##");
                    Console.WriteLine("###########################################################################################");

                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                if (bugsnagEnabled)
                {
                    _bugsnag?.Notify(ex);
                }
            }

            var apiCircuits = F1ApiClient.FetchCircuitCountriesAsync(_httpClient).GetAwaiter().GetResult();
            MergeCircuitCountries(apiCircuits);

            if (enableHardlinking)
            {
                _timer.Interval = 10000;
                _timer.Elapsed += _checkEvents;
                _timer.AutoReset = false;
                Console.WriteLine("[Hardlinking] Enabled — timer will start when a release is queued.");
            }
            else
            {
                Console.WriteLine("[Hardlinking] Disabled — Sonarr will handle file management.");
            }

            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            WebApplication app = builder.Build();

            _ = app.Use(async (context, next) =>
            {
                string pathAndQuery = context.Request.GetEncodedPathAndQuery();

                // Health endpoint: open, no auth, returns only non-sensitive fields
                // so it's safe for Docker healthchecks, Uptime Kuma, etc. to poll
                // without leaking URLs, API keys, file paths, or grab history.
                if (context.Request.Method == "GET" &&
                    (pathAndQuery == "/health" || pathAndQuery.StartsWith("/health?", StringComparison.Ordinal)))
                {
                    var health = new
                    {
                        status = "ok",
                        version = "v0.5.0-fix17",
                        uptimeSeconds = (long)(DateTime.UtcNow - _startedAt).TotalSeconds,
                        torrentClient = TorrentClient ?? "none",
                        sonarrConfigured = !string.IsNullOrEmpty(BaseSonarPath) && !string.IsNullOrEmpty(SonarApiKey),
                        hardlinkingEnabled = enableHardlinking,
                        releasesInQueue = _hashes.Count,
                    };
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsJsonAsync(health);
                    return;
                }

                const string apiEndpoint = "/api";
                if (!pathAndQuery.StartsWith(apiEndpoint))
                {
                    //continues through the rest of the pipeline
                    await next();
                }
                else
                {
                    if (!_httpClient.DefaultRequestHeaders.Contains("X-Api-Key"))
                    {
                        _httpClient.DefaultRequestHeaders.Accept.Clear();
                        _httpClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Formulaar1");
                        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", SonarApiKey);
                    }

                    if (context.Request.Method == "GET")
                    {
                        HttpResponseMessage response = await _httpClient.GetAsync(BaseSonarPath + "/api" + pathAndQuery.Replace(apiEndpoint, ""));

                        string result = await response.Content.ReadAsStringAsync();

                        context.Response.StatusCode = (int)response.StatusCode;
                        await context.Response.WriteAsync(result);
                    }
                    else if (context.Request.Method == "POST")
                    {

                        var tmpReleasePost = await context.Request.ReadFromJsonAsync<POSTReleasePush>();

                        if (tmpReleasePost != null && tmpReleasePost.Protocol != null)
                        {
                            Console.WriteLine($"Processing {tmpReleasePost.SeriesTitle}");

                            _ = Enum.TryParse(char.ToUpper(tmpReleasePost.Protocol[0]) + tmpReleasePost.Protocol.Substring(1), out DownloadProtocol Protocol);

                            var ReleasePost = new ReleaseResource
                            {
                                Title = tmpReleasePost.Title,
                                DownloadUrl = tmpReleasePost.DownloadUrl,
                                Protocol = Protocol,
                                Indexer = tmpReleasePost.Indexer,
                                PublishDate = tmpReleasePost.PublishDate,
                                Size = tmpReleasePost.Size,
                                SeriesTitle = tmpReleasePost.SeriesTitle,
                            };

                            try
                            {
                                var normalisedTitle = ReleasePost.Title?.Replace(".", " ").Replace("-", " ") ?? string.Empty;
                                var seriesInfo = DetectSeries(normalisedTitle);

                                if (ReleasePost != null && ReleasePost.Title != null && seriesInfo != null)
                                {
                                    _ = int.TryParse(Regex.Match(normalisedTitle, @"(?:(?:18|19|20|21)[0-9]{2})").ToString(), out int SeasonID);
                                    var Country = Countries.FirstOrDefault(x => normalisedTitle.Contains(x.Key, StringComparison.OrdinalIgnoreCase)).Value;

                                    var ShowType = NormaliseShowType(normalisedTitle);
                                    Console.WriteLine($"ShowType: {ShowType}");

                                    if (ShowType == null)
                                    {
                                        Console.WriteLine($"Dropping unrecognised/unwanted session type: {ReleasePost.Title}");
                                    }
                                    else if (Country != null)
                                    {
                                        // Series lookup goes through the shim instead of _seriesApi to
                                        // dodge the bundled client's MediaCoverTypes deserializer, which
                                        // throws on "clearlogo" (Sonarr v4 schema). See SonarrSeriesShim.cs.
                                        var Series = await SonarrSeriesShim.GetByTvdbIdAsync(
                                            _httpClient, BaseSonarPath!, SonarApiKey!, seriesInfo.TvdbId);

                                        if (Series.Count == 0 || Series[0].Id == null)
                                        {
                                            Console.WriteLine($"[Sonarr] No series found for tvdbId {seriesInfo.TvdbId} -- is the series added in Sonarr?");
                                        }
                                        else
                                        {
                                        Console.WriteLine($"[Sonarr] Resolved series '{Series[0].Title}' for tvdbId {seriesInfo.TvdbId} -> seriesId {Series[0].Id}");

                                        //Get all Episodes (via shim so we don't depend on the abandoned SDK
                                        // deserialiser even for the read paths that haven't yet broken).
                                        var tmp = await SonarrEpisodeShim.GetBySeriesIdAsync(
                                            _httpClient, BaseSonarPath!, SonarApiKey!, Series[0].Id);
                                        //Find Correct Year
                                        var tmp1 = tmp.Where(x => x.SeasonNumber == SeasonID);
                                        //Find Correct Country
                                        var tmp2 = tmp1.Where(x => (x.Title ?? string.Empty).Contains(Country, StringComparison.OrdinalIgnoreCase));
                                        //Find correct session episode
                                        var tmp3 = GetEpisodesByShowType(tmp2, seriesInfo.Title, ShowType);

                                        var Episode = tmp3.FirstOrDefault();

                                        if (Episode != null)
                                        {
                                            // Inject SxxExx into the ORIGINAL release title rather than
                                            // fully rewriting it. Sonarr's parser only needs an SxxExx
                                            // marker to map the release to the correct TVDB episode;
                                            // everything else in the title (release group, source/codec,
                                            // and indexer-specific markers like F1TV / F1LIVE / SKY / MWR)
                                            // is what Sonarr's Custom Formats use for scoring.
                                            //
                                            // The previous rewrite stripped all of that out, so every
                                            // release got Custom Format score 0 and the user's curated
                                            // per-source / per-group preferences were never applied.
                                            // Preserving the original title means Custom Formats fire
                                            // again and Sonarr can pick a preferred source from several
                                            // simultaneous offers for the same episode.
                                            //
                                            // This also subsumes the earlier "source tag" patch: WEB-DL /
                                            // BluRay / HDTV / etc. ride along in the original title
                                            // naturally, so Sonarr's quality parser sees them too.
                                            var originalTitle = ReleasePost.Title!;

                                            // Same reason as the GetByTvdbId call above -- bypass the
                                            // bundled deserializer for series-by-id too.
                                            var SeriesMap = await SonarrSeriesShim.GetByIdAsync(
                                                _httpClient, BaseSonarPath!, SonarApiKey!, Episode.SeriesId);

                                            if (SeriesMap != null)
                                            {
                                                var SceneMapping = new AlternateTitleResource() { Title = Episode.Title, SeasonNumber = Episode.SeasonNumber, SceneSeasonNumber = Episode.SceneSeasonNumber };

                                                ReleasePost.SceneMapping = SceneMapping;
                                                ReleasePost.TvdbId = SeriesMap.TvdbId;
                                                ReleasePost.Title = $"{SeriesMap.Title} - S{Episode.SeasonNumber}E{string.Format("{0:00}", Episode.EpisodeNumber)} - {originalTitle}";
                                                ReleasePost.SeriesId = SeriesMap.Id;
                                                ReleasePost.SeasonNumber = Episode.SeasonNumber;
                                                ReleasePost.EpisodeNumbers = new List<int?>() { Episode.EpisodeNumber };
                                            }
                                        }
                                        } // closes `else` (series found) introduced by clearlogo shim patch
                                    }
                                    else
                                    {
                                        Console.WriteLine($"No matching country found in: {ReleasePost.Title}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.ToString());
                                if (bugsnagEnabled)
                                {
                                    _bugsnag?.Notify(ex);
                                }
                            }

                            try
                            {
                                var response = await _releasePushApi!.ApiV3ReleasePushPostAsync(ReleasePost);

                                if (response != null)
                                {
                                    Console.WriteLine($"[Sonarr] Push response: {response.Count} decision(s)");
                                    foreach (var r in response)
                                    {
                                        if (r.Rejected == false)
                                        {
                                            Console.WriteLine($"[Sonarr] ACCEPTED: {r.Title}");
                                            // Normalise InfoHash to lower case at the source so all later
                                            // comparisons (against qBit's lower-cased Hash field) just
                                            // work. Sonarr returns it upper-cased.
                                            if (!string.IsNullOrEmpty(r.InfoHash)) r.InfoHash = r.InfoHash.ToLower();
                                            if (!string.IsNullOrEmpty(r.Title))
                                            {
                                                // Atomic add via the dictionary; AddOrUpdate handles the
                                                // edge case of an exact-same Title being pushed twice
                                                // (we just refresh the entry, no double-processing).
                                                _hashes[r.Title] = r;
                                                _queuedAt[r.Title] = DateTime.UtcNow;
                                            }
                                            if (enableHardlinking && !_timer.Enabled)
                                            {
                                                _timer.Start();
                                                Console.WriteLine("[Hardlinking] Release queued — starting download monitor.");
                                            }
                                        }
                                        else
                                        {
                                            // Surface Sonarr's rejection reasons inline. Without this,
                                            // pushes that Sonarr silently rejects (episode not found,
                                            // quality profile mismatch, already grabbed, etc.) appear
                                            // healthy in Formulaar1's logs but never actually download.
                                            // In this SDK version Rejections is List<string>, not a list
                                            // of {Reason, Type} objects -- the strings are already the
                                            // human-readable rejection reasons.
                                            var reasons = (r.Rejections != null && r.Rejections.Count > 0)
                                                ? string.Join(" | ", r.Rejections)
                                                : "(no rejection details returned by Sonarr)";
                                            Console.WriteLine($"[Sonarr] REJECTED: {r.Title} -- {reasons}");
                                        }
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("[Sonarr] Push response was null -- check Sonarr connectivity / API key.");
                                }

                                var result = response;

                                Console.WriteLine($"Pushing to Sonarr: {ReleasePost?.Title}");

                                await context.Response.WriteAsJsonAsync(result);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.ToString());
                                if (bugsnagEnabled)
                                {
                                    _bugsnag?.Notify(ex);
                                }
                            }
                        }
                    }
                }
            });

            app.Run();
        }

        [DllImport("libc", EntryPoint = "link")]
        static extern int link_unix(string oldpath, string newpath);

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        private static int HardLink(string oldpath, string newpath)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return CreateHardLinkW(newpath, oldpath, IntPtr.Zero) ? 0 : -1;
            }
            return link_unix(oldpath, newpath);
        }

        /// <summary>
        /// Tells Sonarr to import the hardlinked file. Tries the manualimport
        /// API first (which links the import to the queue entry via downloadId
        /// so the queue clears atomically), and falls back to the older
        /// DownloadedEpisodesScan command + post-hoc queue DELETE if
        /// manualimport doesn't yield importable suggestions or errors.
        ///
        /// Both paths produce the same end-of-disk state: file moved/linked
        /// into the proper Season folder under Sonarr's naming convention.
        /// They differ only in how the queue tracking entry gets cleared.
        /// </summary>
        private static async Task ImportViaSonarr(string hardpath, string infoHash, string torrentName)
        {
            bool importedViaManual = false;

            try
            {
                // GET without downloadId on purpose -- passing downloadId here
                // filtered all suggestions to zero in fix15 testing. We still
                // set item["downloadId"] before CommitAsync below so the POST
                // links the import to the queue entry.
                var suggestions = await SonarrManualImportShim.GetSuggestionsAsync(
                    _httpClient, BaseSonarPath!, SonarApiKey!, hardpath);

                Console.WriteLine($"[ManualImport] GET returned {suggestions.Count} suggestion(s) for {hardpath}");

                var importable = new List<JObject>();
                foreach (var item in suggestions)
                {
                    // Items Sonarr knows it can't accept (wrong quality profile,
                    // episode already has a higher-scoring file, etc.) come
                    // back with a non-empty rejections array. Surface those
                    // reasons in the log so the user can see what Sonarr
                    // didn't like, but don't try to POST them back.
                    var rejections = item["rejections"] as JArray;
                    if (rejections != null && rejections.Count > 0)
                    {
                        var reasons = string.Join("; ",
                            rejections.Select(r => r["reason"]?.ToString() ?? "(unspecified)"));
                        Console.WriteLine($"[ManualImport] Skipping rejected item '{item["name"]}': {reasons}");
                        continue;
                    }

                    // Sonarr's POST manualimport handler reads seriesId and
                    // episodeIds as FLAT top-level fields. The nested series.id
                    // and episodes[].id in the GET response are informational --
                    // the POST deserializer ignores them and defaults the flat
                    // fields to 0 if missing, which fails the import with
                    // "Series with ID 0 does not exist". Extract from the
                    // nested objects (which the GET correctly populated) and
                    // promote to top-level. Confirmed in fix16/fix17 testing
                    // against Sonarr's live /api/v3/manualimport endpoint.
                    var nestedSeriesId = item["series"]?["id"]?.Value<int?>();
                    if (nestedSeriesId.HasValue)
                        item["seriesId"] = nestedSeriesId.Value;

                    if (item["episodes"] is JArray episodesArr)
                    {
                        var episodeIdArr = new JArray(
                            episodesArr.Select(e => e["id"])
                                       .Where(id => id != null && id.Type != JTokenType.Null)
                                       .ToArray());
                        item["episodeIds"] = episodeIdArr;
                    }

                    // Ensure downloadId is set on every item so Sonarr can link
                    // this manual import to the queue entry from the original
                    // release-push (the whole point of using this endpoint).
                    // "auto" import mode lets Sonarr's media-management
                    // settings decide between move/hardlink/copy.
                    item["downloadId"] = infoHash;
                    item["importMode"] = "auto";
                    importable.Add(item);
                }

                if (importable.Count > 0)
                {
                    var (ok, detail) = await SonarrManualImportShim.CommitAsync(
                        _httpClient, BaseSonarPath!, SonarApiKey!, importable);
                    if (ok)
                    {
                        Console.WriteLine($"[ManualImport] Imported {importable.Count} file(s) via manualimport API; queue should clear automatically");
                        importedViaManual = true;
                    }
                    else
                    {
                        var snippet = detail.Length > 200 ? detail.Substring(0, 200) + "..." : detail;
                        Console.WriteLine($"[ManualImport] POST returned non-success: {snippet} -- falling back to scan+cleanup");
                    }
                }
                else
                {
                    Console.WriteLine($"[ManualImport] No importable suggestions ({suggestions.Count} total, all rejected or unparseable) -- falling back to scan+cleanup");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManualImport] Failed ({ex.Message}) -- falling back to scan+cleanup");
            }

            if (importedViaManual) return;

            // Fallback: classic scan command + post-hoc queue DELETE. Kept as
            // a safety net in case manualimport doesn't apply (Sonarr API
            // change, an edge case in our payload, network blip mid-request).
            // Worth keeping deliberately even though fix14 has been stable in
            // testing -- the two paths converge on identical on-disk state;
            // the fallback only differs in HOW the queue clears (scan + DELETE
            // vs manualimport's atomic downloadId linkage). If you ever see
            // the "falling back to scan+cleanup" log line in normal operation
            // (not "file already in library" edge), investigate before deleting.
            var commandResource = new CommandResource
            {
                Name = "DownloadedEpisodesScan",
                Path = hardpath,
                ImportMode = CommandResource.ImportModeEnum.Auto
            };
            await _commandApi!.ApiV3CommandPostAsync(commandResource);
            Console.WriteLine($"Sending Command:{commandResource.Name} Mode:{commandResource.ImportMode} Torrent:{torrentName} for path \"{commandResource.Path}\"");

            try
            {
                await Task.Delay(5000);
                var stale = await SonarrQueueShim.GetByDownloadIdAsync(_httpClient, BaseSonarPath!, SonarApiKey!, infoHash);
                foreach (var q in stale)
                {
                    if (q.Id is int qid)
                    {
                        await SonarrQueueShim.DeleteAsync(_httpClient, BaseSonarPath!, SonarApiKey!,
                            qid, removeFromClient: false, blocklist: false);
                        Console.WriteLine($"[Hardlinking] Removed stale Sonarr queue item {qid} ('{q.Title}') -- file already imported via scan");
                    }
                }
                if (stale.Count == 0)
                {
                    Console.WriteLine($"[Hardlinking] No matching queue item to clean up for {infoHash} (Sonarr may have already cleared it)");
                }
            }
            catch (Exception cleanupEx)
            {
                Console.WriteLine($"[Hardlinking] Queue cleanup failed (file is imported anyway): {cleanupEx.Message}");
            }
        }

        private static async void _checkEvents(object? sender, System.Timers.ElapsedEventArgs e)
        {
            // Outer try/finally ensures the timer always reschedules. async void
            // handlers can swallow exceptions; without the finally any uncaught
            // throw from the foreach body would silently kill the monitor.
            // (Note: the upstream `running` flag was redundant since
            // Timer.AutoReset=false means single-fire; removing it.)
            try
            {
                if (_hashes.IsEmpty) return;

                Console.WriteLine($"[Hardlinking] Monitor tick: {_hashes.Count} release(s) in queue");

                foreach (var r in _hashes.Values.ToList())
                {
                    try
                    {
                    // Stuck-release eviction. If a release has been in _hashes
                    // for more than 24 hours and never resolved, something's
                    // genuinely wrong (qBit dropped the torrent, hash mismatch
                    // we can't fix, Sonarr/qBit unreachable, etc.) -- evict it
                    // so the monitor stops re-checking it forever.
                    if (!string.IsNullOrEmpty(r.Title) &&
                        _queuedAt.TryGetValue(r.Title, out var queuedAt) &&
                        (DateTime.UtcNow - queuedAt).TotalHours > 24)
                    {
                        Console.WriteLine($"[Hardlinking] Evicting stuck release after >24h: '{r.Title}' (never resolved to a completed qBit torrent). Check qBit and Sonarr connectivity.");
                        _hashes.TryRemove(r.Title, out _);
                        _queuedAt.TryRemove(r.Title, out _);
                        continue;
                    }

                    Console.WriteLine($"[Hardlinking] Processing release '{r.Title}' (InfoHash={(r.InfoHash ?? "<null>")})");
                    if (r.InfoHash == null)
                    {
                        // Go through the shim instead of _historyApi: the bundled
                        // APIv3SonarrDotcore client crashes deserializing modern Sonarr
                        // history responses (same MediaCoverTypes "clearlogo" issue as
                        // the series endpoint). Wrap in try/catch defensively so even
                        // an unexpected failure here can't kill the timer loop.
                        try
                        {
                            var history = await SonarrHistoryShim.GetRecentAsync(
                                _httpClient, BaseSonarPath!, SonarApiKey!);

                            // Build the SxxExx marker we injected into the release title.
                            // Matching on this rather than full SourceTitle equality is
                            // robust to any normalisation Sonarr applies (whitespace,
                            // trailing chars) and uses an invariant we control.
                            var seasonNum = r.SeasonNumber ?? 0;
                            var episodeNum = r.EpisodeNumbers?.FirstOrDefault() ?? 0;
                            var sxxexx = $"S{seasonNum}E{episodeNum:00}";

                            // Sonarr's history endpoint returns Date as UTC (ISO 8601
                            // with 'Z'), which Newtonsoft deserialises with Kind=Utc.
                            // Upstream compared against DateTime.Now (local), and C#'s
                            // DateTime comparison ignores Kind -- it compares raw ticks.
                            // On any container with a non-UTC TZ (e.g. America/New_York
                            // = EDT -4h), every Sonarr record looks ~4h in the future
                            // relative to local "now - 1 minute" and is filtered out.
                            // Result: monitor ticks forever, history scan finds nothing,
                            // InfoHash never resolves, hardlink never runs. Compare
                            // against UtcNow so this works regardless of container TZ.
                            // (Also relaxed the cushion from 1min to 30s -- enough for
                            // qBit to register the torrent without making testing slow.)
                            var grabs = history.Records
                                .Where(x => x.Date < DateTime.UtcNow.AddSeconds(-30) &&
                                            string.Equals(x.EventType, "grabbed", StringComparison.OrdinalIgnoreCase) &&
                                            !string.IsNullOrEmpty(x.SourceTitle) &&
                                            x.SourceTitle.Contains(sxxexx, StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(x => x.Date)
                                .ToList();

                            if (grabs.Count == 0)
                            {
                                // Diagnostic: dump the 3 most recent grabs so we can see
                                // why our match failed. Cheap once-per-tick log; if you
                                // see this firing every tick after an accepted push,
                                // either the SxxExx isn't actually in SourceTitle or the
                                // date filter is still excluding it.
                                var recent = history.Records
                                    .Where(x => string.Equals(x.EventType, "grabbed", StringComparison.OrdinalIgnoreCase))
                                    .OrderByDescending(x => x.Date)
                                    .Take(3)
                                    .Select(x => $"'{(x.SourceTitle ?? "").Substring(0, Math.Min(70, (x.SourceTitle ?? "").Length))}' @ {x.Date:u}")
                                    .ToList();
                                Console.WriteLine($"[Hardlinking] No grab matching {sxxexx} in history yet. Recent grabs: {(recent.Count == 0 ? "(none)" : string.Join(" | ", recent))}");
                            }
                            else
                            {
                                var h = grabs.First();
                                Console.WriteLine($"[Hardlinking] Resolved InfoHash from Sonarr history: {h.DownloadId} for '{r.Title}' (matched {sxxexx})");
                                r.InfoHash = h.DownloadId?.ToLower();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Hardlinking] History lookup failed for '{r.Title}': {ex.Message}");
                        }
                        // (Vestigial 1-second Task.Delay removed in fix15. It was
                        // there in upstream to let Sonarr's history register a
                        // grab between retries, but with the SxxExx+UTC matcher
                        // from fix7 we now find recent grabs reliably without
                        // sleep loops.)
                    }

                    if (r.InfoHash != null)
                    {
                        try
                        {
                            var query = new TorrentListQuery() { Hashes = new string[] { r.InfoHash } };
                            var result = await _qBittorrentClient!.GetTorrentListAsync(query);

                            // Diagnostic: surface whether qBit has the torrent and
                            // whether it's complete. Both gates were previously silent
                            // dead-ends -- if qBit returned zero matches (e.g. hash
                            // case mismatch, torrent not added yet, or wrong client)
                            // OR the torrent was still downloading (CompletionOn null),
                            // the foreach iteration did nothing and the next monitor
                            // tick repeated the same nothing.
                            if (result.Count == 0)
                            {
                                Console.WriteLine($"[Hardlinking] qBit has no torrent with hash {r.InfoHash} -- still being added? Will retry next tick.");
                            }
                            else
                            {
                                var probe = result.FirstOrDefault();
                                Console.WriteLine($"[Hardlinking] qBit returned {result.Count} torrent(s) for hash {r.InfoHash}: name='{probe?.Name}' completionOn={(probe?.CompletionOn?.ToString("u") ?? "<null, still downloading>")}");
                            }

                            if (result.Count > 0)
                            {
                                var torrent = result.FirstOrDefault();
                                if (torrent != null && torrent.CompletionOn != null)
                                {
                                    // We're already iterating the release that owns this InfoHash --
                                    // the upstream re-lookup against _hashes by torrent.Hash was
                                    // doing the same thing (and had a case-sensitivity bug fix9
                                    // patched). With the ConcurrentDictionary migration in fix15
                                    // we can just use `r` directly: it IS the matched release.
                                    var sonarrItem = r;
                                    {
                                        FileAttributes attr = File.GetAttributes(Path.Combine(torrent.SavePath!, torrent.Name!));

                                        // Sanitize for filesystem use. After the fix4 title-preservation
                                        // change, sonarrItem.Title carries the FULL original release name
                                        // (so Sonarr's Custom Formats can score it at push-time). That
                                        // string can contain characters that explode Path.Combine -- most
                                        // notably the '/' inside indexer-added markers like
                                        // "[SEEDERS (34)/LEECHERS (1)]", which on Linux gets treated as a
                                        // directory separator and creates a nested folder where one was
                                        // expected. Extract the canonical "Series - SxxExx" prefix when
                                        // present (Sonarr's parser only needs SxxExx in the filename to
                                        // map the imported file to the correct episode); otherwise strip
                                        // path-hostile characters from the full title.
                                        var canonicalPrefix = Regex.Match(sonarrItem.Title ?? string.Empty,
                                            @"^.+?\s-\sS\d+E\d+", RegexOptions.IgnoreCase).Value;
                                        var safeTitle = !string.IsNullOrWhiteSpace(canonicalPrefix)
                                            ? canonicalPrefix
                                            : Regex.Replace(sonarrItem.Title ?? "release",
                                                @"[<>:""/\\|?*\[\]\r\n\t]", " ").Trim();

                                        var hardpathcomplete = Path.Combine(Hardlinkpath!, safeTitle);

                                        Directory.CreateDirectory(hardpathcomplete);

                                        if ((attr & FileAttributes.Directory) == FileAttributes.Directory)
                                        {
                                            var files = Directory.GetFiles(Path.Combine(torrent.SavePath!, torrent.Name!));

                                            //Attempt to Hardlink files.
                                            foreach (var file in files)
                                            {
                                                var ofInfo = new FileInfo(file);
                                                var nfInfo = new FileInfo($"{hardpathcomplete}/{safeTitle} - {ofInfo.Name}");

                                                if (ofInfo.Name.ToLower().Contains("buildup"))
                                                {
                                                    nfInfo = new FileInfo($"{hardpathcomplete}/{safeTitle} - Part1{ofInfo.Extension}");

                                                    Console.WriteLine($"Hard Linking {ofInfo.Name} to {nfInfo.Name}");
                                                    int linkResult = HardLink(ofInfo.ToString(), nfInfo.ToString());
                                                    if (linkResult != 0) Console.WriteLine($"Hard link failed (code {linkResult}): {ofInfo.Name}");
                                                }
                                                else if (ofInfo.Name.ToLower().Contains("session"))
                                                {
                                                    nfInfo = new FileInfo($"{hardpathcomplete}/{safeTitle} - Part2{ofInfo.Extension}");

                                                    Console.WriteLine($"Hard Linking {ofInfo.Name} to {nfInfo.Name}");
                                                    int linkResult = HardLink(ofInfo.ToString(), nfInfo.ToString());
                                                    if (linkResult != 0) Console.WriteLine($"Hard link failed (code {linkResult}): {ofInfo.Name}");
                                                }
                                                else if (ofInfo.Name.ToLower().Contains("analysis"))
                                                {
                                                    nfInfo = new FileInfo($"{hardpathcomplete}/{safeTitle} - Part3{ofInfo.Extension}");

                                                    Console.WriteLine($"Hard Linking {ofInfo.Name} to {nfInfo.Name}");
                                                    int linkResult = HardLink(ofInfo.ToString(), nfInfo.ToString());
                                                    if (linkResult != 0) Console.WriteLine($"Hard link failed (code {linkResult}): {ofInfo.Name}");
                                                }
                                                else
                                                {
                                                    if (!File.Exists(nfInfo.ToString()))
                                                    {
                                                        Console.WriteLine($"Hard Linking {ofInfo.Name} to {nfInfo.Name}");
                                                        int linkResult = HardLink(ofInfo.ToString(), nfInfo.ToString());
                                                        if (linkResult != 0) Console.WriteLine($"Hard link failed (code {linkResult}): {ofInfo.Name}");
                                                    }
                                                }
                                            }

                                        }
                                        else
                                        {
                                            var file = Path.Combine(torrent.SavePath!, torrent.Name!);
                                            var ofInfo = new FileInfo(file);
                                            var nameLower = ofInfo.Name.ToLower();

                                            FileInfo nfInfo;
                                            if (nameLower.Contains("buildup"))
                                                nfInfo = new FileInfo($"{hardpathcomplete}/{safeTitle} - Part1{ofInfo.Extension}");
                                            else if (nameLower.Contains("session"))
                                                nfInfo = new FileInfo($"{hardpathcomplete}/{safeTitle} - Part2{ofInfo.Extension}");
                                            else if (nameLower.Contains("analysis"))
                                                nfInfo = new FileInfo($"{hardpathcomplete}/{safeTitle} - Part3{ofInfo.Extension}");
                                            else
                                                nfInfo = new FileInfo($"{hardpathcomplete}/{safeTitle}{ofInfo.Extension}");

                                            if (!File.Exists(nfInfo.ToString()))
                                            {
                                                Console.WriteLine($"Hard Linking File {ofInfo.Name} to {nfInfo.Name}");
                                                int linkResult = HardLink(ofInfo.ToString(), nfInfo.ToString());
                                                if (linkResult != 0) Console.WriteLine($"Hard link failed (code {linkResult}): {ofInfo.Name}");
                                            }
                                        }

                                        // Both branches converge here: hardlinks (or hardlink) are in
                                        // place inside hardpathcomplete. Hand off to ImportViaSonarr
                                        // which prefers the manualimport API (queue clears via
                                        // downloadId linkage) and falls back to the older
                                        // DownloadedEpisodesScan + DELETE path on failure.
                                        await ImportViaSonarr(hardpathcomplete, sonarrItem.InfoHash!, torrent.Name!);

                                        // Atomic remove via the dictionary -- no reassignment race
                                        // (the upstream "new Bag(Except [r])" pattern could drop a
                                        // concurrently-added release into the soon-to-be-orphaned
                                        // old bag).
                                        if (!string.IsNullOrEmpty(r.Title))
                                        {
                                            _hashes.TryRemove(r.Title, out _);
                                            _queuedAt.TryRemove(r.Title, out _);
                                        }
                                        if (_hashes.IsEmpty) Console.WriteLine("[Hardlinking] Queue empty — monitor idle.");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Selective reauth: only re-login on errors that look like
                            // session/auth failures. The upstream code blindly re-logged
                            // on every exception, which is wasteful (network blips, malformed
                            // responses, etc. all triggered an unnecessary login round-trip)
                            // and could mask the original error under a follow-up auth failure.
                            var msg = ex.Message ?? string.Empty;
                            bool looksLikeAuth =
                                msg.Contains("401") || msg.Contains("403") ||
                                msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                                msg.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);
                            if (looksLikeAuth)
                            {
                                Console.WriteLine("[Hardlinking] qBit returned auth error -- attempting re-login");
                                try { await _qBittorrentClient!.LoginAsync(qBitUsername, qBitPassword); }
                                catch (Exception loginEx) { Console.WriteLine($"[Hardlinking] Re-login failed: {loginEx.Message}"); }
                            }
                            Console.WriteLine($"[Hardlinking] qBit error processing '{r.Title}': {ex}");
                            if (bugsnagEnabled)
                            {
                                _bugsnag?.Notify(ex);
                            }
                        }
                    }
                    } // closes per-release try
                    catch (Exception perReleaseEx)
                    {
                        // Final safety net for anything thrown OUTSIDE the inner try
                        // blocks (Path.Combine on weird input, DirectoryNotFoundException
                        // on missing share, etc.). Logs and moves to next release rather
                        // than letting one bad entry kill the whole monitor.
                        Console.WriteLine($"[Hardlinking] Unexpected error processing '{r.Title}': {perReleaseEx.Message}");
                    }
                }
            }
            catch (Exception outerEx)
            {
                Console.WriteLine($"[Hardlinking] Monitor tick crashed: {outerEx.Message}");
            }
            finally
            {
                // Always reschedule if there's still work. The finally block makes
                // sure an exception anywhere above can't strand the timer.
                if (!_hashes.IsEmpty)
                {
                    try { _timer.Start(); } catch { /* timer disposed during shutdown */ }
                }
            }
        }

    }
}