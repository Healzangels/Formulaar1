using APIv3SonarrDotcore.Api;
using APIv3SonarrDotcore.Model;
using Microsoft.AspNetCore.Http.Extensions;
using Newtonsoft.Json.Linq;
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
        // UseCookies=false so Set-Cookie comes through to resp.Headers (needed
        // by QBittorrentShim's manual session-cookie parse). Default
        // HttpClientHandler consumes Set-Cookie into a CookieContainer and
        // doesn't expose it to caller code, which broke qBit login in fix19.
        // Sonarr API uses X-Api-Key (no cookies), qBit shim manages cookies
        // manually via Cookie: header, F1API is unauthenticated -- nothing
        // else in the codebase needs auto-cookies.
        private static HttpClient _httpClient = new(new SocketsHttpHandler { UseCookies = false });

        // qBit session cookie as a full 'name=value' pair (e.g.
        // "QBT_SID_8080=abc123..."), populated by QBittorrentShim.LoginAsync at
        // startup. Stored verbatim so the shim can drop it straight into a
        // Cookie: request header without knowing whether qBit gave us the
        // legacy 'SID' or the port-namespaced 'QBT_SID_<port>' form (fix21).
        // Replaces the QBittorrent.Client SDK which couldn't handle qBit 5.0+
        // state enum changes (stoppedDL etc.) -- see QBittorrentShim.cs.
        private static string? _qBitSid;

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

        // Which Sonarr import path to use after a hardlink succeeds. Added in
        // fix22 as a feature flag so we can A/B test the atomic manualimport
        // API (fix14-17 work, reverted in fix18) against the proven
        // DownloadedEpisodesScan command (fix18-21 current default).
        // Values: "scan" (default, ships the scan command + smart queue
        // cleanup) or "manualimport" (POSTs to /api/v3/manualimport + smart
        // queue cleanup as a backstop). Anything unrecognised falls back to
        // "scan" so a typo in config can't strand imports.
        private static string _importMode = "scan";

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

            // ImportMode: opt-in switch between the proven scan command path
            // (default) and the atomic manualimport API. Normalised to lower
            // case so "ScAn" / "ManualImport" / etc. all work, and validated
            // against the known set -- typos collapse to "scan" with a warning
            // so misconfiguration never silently disables imports.
            var importModeRaw = config.GetValue<string>("ImportMode")?.Trim().ToLowerInvariant();
            if (importModeRaw == "manualimport" || importModeRaw == "scan")
            {
                _importMode = importModeRaw;
            }
            else if (!string.IsNullOrEmpty(importModeRaw))
            {
                Console.WriteLine($"[Config] Unknown ImportMode '{importModeRaw}' -- falling back to 'scan'. Valid values: 'scan', 'manualimport'.");
            }
            Console.WriteLine($"[Config] Import mode: {_importMode}");

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
                    // Direct HTTP via QBittorrentShim (replaces the abandoned
                    // QBittorrent.Client SDK which couldn't deserialise qBit
                    // 5.0's new state names like 'stoppedDL').
                    _qBitSid = QBittorrentShim.LoginAsync(_httpClient, BaseqBitPath!, qBitUsername, qBitPassword).GetAwaiter().GetResult();
                    if (_qBitSid == null)
                    {
                        Console.WriteLine("!!  qBit login failed -- check qBittorrentClient username/password/BasePath in appsettings.json !!");
                    }
                    else
                    {
                        var ver = QBittorrentShim.GetVersionAsync(_httpClient, BaseqBitPath!, _qBitSid).GetAwaiter().GetResult();
                        Console.WriteLine($"Logged in to {ver}");
                    }
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
                        version = "v0.5.0-fix25",
                        uptimeSeconds = (long)(DateTime.UtcNow - _startedAt).TotalSeconds,
                        torrentClient = TorrentClient ?? "none",
                        sonarrConfigured = !string.IsNullOrEmpty(BaseSonarPath) && !string.IsNullOrEmpty(SonarApiKey),
                        hardlinkingEnabled = enableHardlinking,
                        importMode = _importMode,
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
        /// Tells Sonarr to import the hardlinked file, then runs the smart
        /// queue cleanup. Dispatches between two import paths based on the
        /// <c>ImportMode</c> config flag (fix22):
        ///
        /// <list type="bullet">
        /// <item>
        ///   <term><c>scan</c> (default)</term>
        ///   <description>Issues a <c>DownloadedEpisodesScan</c> command.
        ///   Goes through Sonarr's normal import pipeline -- fires
        ///   EpisodeFileImported events, pushes SignalR notifications to the
        ///   web UI, updates Episode.HasFile so the series page reflects the
        ///   imported file without manual refresh. Proven path; what fix18-21
        ///   shipped as the only option.</description>
        /// </item>
        /// <item>
        ///   <term><c>manualimport</c></term>
        ///   <description>POSTs to <c>/api/v3/manualimport</c>. Faster (no
        ///   filesystem scan), more precise (we tell Sonarr exact episode
        ///   IDs instead of relying on its parser to extract SxxExx from the
        ///   hardlinked filename), and avoids spinning up storage drives.
        ///   Tried in fix14-17 and reverted in fix18 due to a "no UI update
        ///   without manual refresh" regression that was never root-caused --
        ///   reintroduced as opt-in in fix22 to A/B test whether that
        ///   regression was actually caused by the aggressive DELETE timing
        ///   from fix12-17 (since fixed by fix19's smart cleanup).</description>
        /// </item>
        /// </list>
        ///
        /// <para>
        /// Both paths share the same smart queue cleanup (fix19):
        /// after the import action, wait 30s for Sonarr's polling to settle,
        /// then DELETE only queue entries Sonarr has flagged as stuck
        /// (errorMessage set or trackedDownloadStatus = warning/error).
        /// Healthy entries are left alone for Sonarr's own lifecycle to
        /// resolve.
        /// </para>
        /// </summary>
        private static async Task ImportViaSonarr(string hardpath, string infoHash, string torrentName)
        {
            if (_importMode == "manualimport")
            {
                // fix24: ImportViaManualImportApi owns its own queue cleanup on
                // the happy path -- it polls the command to completion and
                // DELETEs the queue entry synchronously when Sonarr reports
                // success.
                //
                // fix25: but if anything goes wrong (HTTP error, command
                // result=unsuccessful, polling timeout, no importable
                // suggestions, exception), it returns false and we still need
                // the 30s smart cleanup so stuck queue entries don't get
                // orphaned. The file is on disk either way (hardlink happened
                // before dispatch), the fallback just makes Sonarr's view
                // consistent.
                var handled = await ImportViaManualImportApi(hardpath, infoHash);
                if (!handled)
                {
                    await CleanupSonarrQueue(infoHash);
                }
            }
            else
            {
                await ImportViaScanCommand(hardpath, torrentName);
                // Scan mode has no per-file completion signal (DownloadedEpisodesScan
                // is a folder operation that doesn't surface per-file results), so
                // we fall back to the timed smart cleanup which catches CDH's
                // warning-state entry after the scan has had time to land.
                await CleanupSonarrQueue(infoHash);
            }
        }

        private static async Task ImportViaScanCommand(string hardpath, string torrentName)
        {
            var commandResource = new CommandResource
            {
                Name = "DownloadedEpisodesScan",
                Path = hardpath,
                ImportMode = CommandResource.ImportModeEnum.Auto
            };
            await _commandApi!.ApiV3CommandPostAsync(commandResource);
            Console.WriteLine($"Sending Command:{commandResource.Name} Mode:{commandResource.ImportMode} Torrent:{torrentName} for path \"{commandResource.Path}\"");
        }

        /// <summary>
        /// Atomic import via <c>/api/v3/manualimport</c>. Two-step flow:
        /// <list type="number">
        /// <item>GET candidates from the folder. We deliberately omit
        ///   downloadId here -- fix15 testing showed Sonarr filters to 0
        ///   results when downloadId is set on a folder that isn't qBit's
        ///   download path. See SonarrManualImportShim docs.</item>
        /// <item>For each candidate Sonarr didn't reject: promote nested
        ///   series.id and episodes[].id to flat top-level seriesId and
        ///   episodeIds (fix17 lesson -- the POST handler reads flat fields
        ///   and defaults the missing ones to 0, yielding
        ///   "Series ID 0 does not exist" if we don't promote). Attach
        ///   downloadId and importMode=auto, then POST the batch back.</item>
        /// </list>
        /// Any failure here falls through to the smart cleanup, which catches
        /// the queue entry on the warning/error path -- the file is still
        /// on disk from the hardlink either way.
        /// </summary>
        /// <summary>
        /// Returns <c>true</c> only if the import succeeded AND we synchronously
        /// deleted the queue entry (fix24's happy path). Returns <c>false</c> on
        /// any failure -- HTTP error on GET/POST, no importable suggestions,
        /// command id missing, command polling timeout, command result
        /// unsuccessful, or exceptions. fix25: the caller (ImportViaSonarr)
        /// uses this signal to decide whether to run the 30s fallback cleanup
        /// so stuck queue entries from failed imports don't get orphaned.
        /// </summary>
        private static async Task<bool> ImportViaManualImportApi(string hardpath, string infoHash)
        {
            try
            {
                var suggestions = await SonarrManualImportShim.GetSuggestionsAsync(
                    _httpClient, BaseSonarPath!, SonarApiKey!, hardpath);
                Console.WriteLine($"[ManualImport] GET returned {suggestions.Count} suggestion(s) for {hardpath}");

                var importable = new List<JObject>();
                foreach (var item in suggestions)
                {
                    // Sonarr flags rejected items (wrong quality profile, existing
                    // higher-scoring file, etc.) with a non-empty rejections array.
                    // Log the reasons but skip POSTing them back.
                    var rejections = item["rejections"] as JArray;
                    if (rejections != null && rejections.Count > 0)
                    {
                        var reasons = string.Join("; ",
                            rejections.Select(r => r["reason"]?.ToString() ?? "(unspecified)"));
                        Console.WriteLine($"[ManualImport] Skipping rejected item '{item["name"]}': {reasons}");
                        continue;
                    }

                    // fix17: Sonarr's handler reads flat seriesId and episodeIds.
                    // The nested series.id and episodes[].id in the GET response are
                    // informational; missing flat fields default to 0 and the import
                    // fails with "Series with ID 0 does not exist."
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

                    // fix24: ALWAYS derive folderName from path (don't trust Sonarr's
                    // GET-response value). Empirical: Sonarr returns folderName as a
                    // BARE name like "Formula 1 - S2026E34" (relative), but the
                    // command-bus ManualImportCommand needs the absolute directory.
                    // Overwrite unconditionally with Path.GetDirectoryName(item.path),
                    // which IS absolute since we GET the folder we know to be absolute.
                    {
                        var path = item["path"]?.ToString();
                        if (!string.IsNullOrEmpty(path))
                        {
                            var folder = Path.GetDirectoryName(path);
                            if (!string.IsNullOrEmpty(folder))
                                item["folderName"] = folder;
                        }
                    }

                    // downloadId on each item links the import event back to the
                    // queue entry from the original release-push so Sonarr can
                    // clear queue tracking as a side effect of the import.
                    item["downloadId"] = infoHash;
                    item["importMode"] = "auto";

                    // quality / languages / releaseGroup / indexerFlags / customFormats
                    // are already populated on the GET response items by Sonarr's parser
                    // -- we don't need to fabricate them, just let them ride through the
                    // POST as-is. Log what we have so the next failure is debuggable.
                    var qualityName = item["quality"]?["quality"]?["name"]?.ToString() ?? "<none>";
                    var langs = item["languages"] is JArray langArr
                        ? string.Join(",", langArr.Select(l => l["name"]?.ToString() ?? "?"))
                        : "<none>";
                    var releaseGroup = item["releaseGroup"]?.ToString() ?? "<none>";
                    Console.WriteLine($"[ManualImport] Item enriched: seriesId={item["seriesId"]} episodeIds={item["episodeIds"]} quality='{qualityName}' languages='{langs}' releaseGroup='{releaseGroup}' folderName='{item["folderName"]}'");

                    importable.Add(item);
                }

                if (importable.Count == 0)
                {
                    Console.WriteLine($"[ManualImport] No importable suggestions ({suggestions.Count} total, all rejected or unparseable). Falling back to smart cleanup pass.");
                    return false;
                }

                // Dispatch via the command bus (/api/v3/command) so Sonarr fires
                // EpisodeFileImported / SeriesUpdated SignalR events (fix23). Returns
                // the command id, which fix24 uses to poll for completion.
                var dispatch = await SonarrManualImportShim.CommitViaCommandAsync(
                    _httpClient, BaseSonarPath!, SonarApiKey!, importable);
                if (!dispatch.Success)
                {
                    var snippet = dispatch.Detail.Length > 200 ? dispatch.Detail.Substring(0, 200) + "..." : dispatch.Detail;
                    Console.WriteLine($"[ManualImport] Command-bus POST returned non-success: {snippet}. Falling back to smart cleanup pass.");
                    return false;
                }

                Console.WriteLine($"[ManualImport] Dispatched ManualImport command (id={dispatch.CommandId?.ToString() ?? "<unknown>"}) for {importable.Count} file(s)");

                if (dispatch.CommandId is not int cmdId)
                {
                    Console.WriteLine($"[ManualImport] Command id missing from dispatch response; falling back to smart cleanup pass");
                    return false;
                }

                // fix24: poll the command to completion so we know whether the
                // import actually succeeded -- instead of inferring from "cleanup
                // found a warning queue entry, must have worked." Caps at ~30s of
                // polling at 1s intervals; if Sonarr is still chewing on it after
                // that we move on and let smart cleanup mop up.
                var commandSucceeded = await PollCommandToCompletion(cmdId, maxWaitSeconds: 30);
                if (!commandSucceeded)
                {
                    // Polling already logged status/exception. Fall back to the
                    // smart cleanup so any stuck queue entry doesn't get
                    // orphaned -- the file is on disk either way (we hardlinked
                    // before dispatch), the cleanup just makes Sonarr's view
                    // consistent.
                    return false;
                }

                // fix24: race CDH for the queue DELETE. When we delete the
                // queue entry IMMEDIATELY after our import succeeds, we beat
                // (or at least quickly clear) Sonarr's CDH from flipping the
                // tracked download into the sticky "warning" state. Net UX:
                // queue transitions downloading -> (deleted) without the
                // intervening "failed/warning" flash the user saw before.
                await DeleteQueueEntryForDownload(infoHash, reason: "ManualImport command succeeded");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManualImport] Failed: {ex.Message}. Falling back to smart cleanup pass.");
                return false;
            }
        }

        /// <summary>
        /// Polls GET /api/v3/command/{id} every second until the command reports
        /// an ended state (completed/failed/aborted/cancelled/orphaned) or the
        /// cap elapses. Logs the result with Sonarr's own exception text when
        /// available -- way more useful for debugging than our previous
        /// "must have worked because the queue is in warning" inference.
        ///
        /// Returns true only when status="completed" AND result="successful".
        /// </summary>
        private static async Task<bool> PollCommandToCompletion(int commandId, int maxWaitSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000);
                try
                {
                    var status = await SonarrManualImportShim.GetCommandStatusAsync(
                        _httpClient, BaseSonarPath!, SonarApiKey!, commandId);
                    if (status == null) continue; // transient HTTP error -- keep polling
                    if (!status.IsEnded) continue;

                    if (status.Status == "completed" && string.Equals(status.Result, "successful", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[ManualImport] Command {commandId} completed successfully");
                        return true;
                    }

                    // Some terminal state that isn't "successful" -- surface
                    // whatever detail Sonarr gave us.
                    var exception = string.IsNullOrWhiteSpace(status.Exception)
                        ? "(no exception text)"
                        : status.Exception;
                    Console.WriteLine($"[ManualImport] Command {commandId} ended with status='{status.Status}' result='{status.Result}' exception={exception}");
                    return false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ManualImport] Error polling command {commandId}: {ex.Message}");
                    // Don't return immediately on transient errors -- the command
                    // is probably still running fine, we just couldn't read its
                    // status this tick.
                }
            }

            Console.WriteLine($"[ManualImport] Command {commandId} didn't finish within {maxWaitSeconds}s -- moving on, smart cleanup pass will tidy up");
            return false;
        }

        /// <summary>
        /// Unconditional DELETE of any queue entry matching the given downloadId.
        /// Used by fix24 after a confirmed-successful ManualImport to race CDH's
        /// warning-flip. removeFromClient=false so qBit keeps seeding for ratio;
        /// blocklist=false so the indexer doesn't get a strike.
        /// </summary>
        private static async Task DeleteQueueEntryForDownload(string infoHash, string reason)
        {
            try
            {
                var queue = await SonarrQueueShim.GetByDownloadIdAsync(_httpClient, BaseSonarPath!, SonarApiKey!, infoHash);
                if (queue.Count == 0)
                {
                    // Could mean Sonarr already cleared it (ManualImport's
                    // tracked-download linkage worked perfectly), or the queue
                    // entry never existed (Sonarr's polling hadn't caught the
                    // download yet). Either way, nothing to do.
                    Console.WriteLine($"[Hardlinking] No queue item to delete for {infoHash} (reason: {reason})");
                    return;
                }
                foreach (var q in queue)
                {
                    if (q.Id is int qid)
                    {
                        await SonarrQueueShim.DeleteAsync(_httpClient, BaseSonarPath!, SonarApiKey!,
                            qid, removeFromClient: false, blocklist: false);
                        Console.WriteLine($"[Hardlinking] Deleted queue item {qid} (reason: {reason}; status was {q.TrackedDownloadStatus}/{q.TrackedDownloadState})");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Hardlinking] Queue delete failed (file is imported either way): {ex.Message}");
            }
        }

        /// <summary>
        /// Smart queue cleanup (fix19, extracted into a helper in fix22).
        /// Waits 30s for Sonarr's polling cycle to settle, then DELETEs only
        /// queue entries flagged as stuck (errorMessage set or
        /// trackedDownloadStatus = warning/error). Healthy entries are left
        /// alone -- Sonarr will clean them up via its own lifecycle.
        /// </summary>
        private static async Task CleanupSonarrQueue(string infoHash)
        {
            try
            {
                await Task.Delay(30000);
                var stale = await SonarrQueueShim.GetByDownloadIdAsync(_httpClient, BaseSonarPath!, SonarApiKey!, infoHash);
                if (stale.Count == 0)
                {
                    Console.WriteLine($"[Hardlinking] No queue item left for {infoHash} -- Sonarr cleared it naturally");
                }
                foreach (var q in stale)
                {
                    bool isStuck =
                        !string.IsNullOrEmpty(q.ErrorMessage) ||
                        string.Equals(q.TrackedDownloadStatus, "warning", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(q.TrackedDownloadStatus, "error", StringComparison.OrdinalIgnoreCase);

                    if (isStuck && q.Id is int qid)
                    {
                        await SonarrQueueShim.DeleteAsync(_httpClient, BaseSonarPath!, SonarApiKey!,
                            qid, removeFromClient: false, blocklist: false);
                        Console.WriteLine($"[Hardlinking] Removed stuck Sonarr queue item {qid} (status: {q.TrackedDownloadStatus}, err: {q.ErrorMessage}). File already imported via {_importMode}.");
                    }
                    else
                    {
                        Console.WriteLine($"[Hardlinking] Queue item {q.Id} for {infoHash} looks healthy (status: {q.TrackedDownloadStatus}/{q.TrackedDownloadState}); leaving for Sonarr to manage");
                    }
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
                            // Go through QBittorrentShim instead of the bundled SDK so
                            // qBit's new state names (stoppedDL etc., qBit 5.0+) don't
                            // crash the deserialiser. The shim treats state as a raw
                            // string and only reads the fields the monitor needs.
                            if (_qBitSid == null)
                            {
                                _qBitSid = await QBittorrentShim.LoginAsync(_httpClient, BaseqBitPath!, qBitUsername!, qBitPassword!);
                            }
                            var result = _qBitSid != null
                                ? await QBittorrentShim.GetByHashAsync(_httpClient, BaseqBitPath!, _qBitSid, r.InfoHash)
                                : new List<QBittorrentShim.MinimalTorrent>();

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
                                try
                                {
                                    _qBitSid = await QBittorrentShim.LoginAsync(_httpClient, BaseqBitPath!, qBitUsername!, qBitPassword!);
                                }
                                catch (Exception loginEx) { Console.WriteLine($"[Hardlinking] Re-login failed: {loginEx.Message}"); }
                            }
                            Console.WriteLine($"[Hardlinking] qBit error processing '{r.Title}': {ex.Message}");
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