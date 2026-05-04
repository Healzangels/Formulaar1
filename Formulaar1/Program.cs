using APIv3SonarrDotcore.Api;
using APIv3SonarrDotcore.Model;
using Microsoft.AspNetCore.Http.Extensions;
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
        private static EpisodeApi? _episodeApi;
        //private static SeasonPassApi? _seasonPassApi;
        private static ReleasePushApi? _releasePushApi;
        //private static QueueStatusApi? _queueStatusApi;
        //private static QueueDetailsApi? _queueDetailsApi;
        private static CommandApi? _commandApi;
        private static HistoryApi? _historyApi;
        private static HttpClient _httpClient = new();

        private static QBittorrentClient? _qBittorrentClient;

        private static ConcurrentBag<ReleaseResource> _hashes = new ConcurrentBag<ReleaseResource>();

        private static System.Timers.Timer _timer = new System.Timers.Timer();

        private static string? TorrentClient, BaseSonarPath, BaseqBitPath, SonarApiKey, qBitUsername, qBitPassword, bugsnagApiKey, Hardlinkpath;

        private static bool running = false;
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
            bugsnagEnabled = config.GetValue<bool>("APICredentials:bugsnag:enabled");
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
                //_seasonPassApi = new SeasonPassApi();
                _episodeApi = new EpisodeApi();
                _releasePushApi = new ReleasePushApi();
                //_queueStatusApi = new QueueStatusApi();
                //_queueDetailsApi = new QueueDetailsApi();
                _historyApi = new HistoryApi();
                _commandApi = new CommandApi();
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
                                        var Series = await _seriesApi!.ApiV3SeriesGetAsync(seriesInfo.TvdbId);
                                        //Get all Episodes
                                        var tmp = await _episodeApi!.ApiV3EpisodeGetAsync(Series[0].Id);
                                        //Find Correct Year
                                        var tmp1 = tmp.Where(x => x.SeasonNumber == SeasonID);
                                        //Find Correct Country
                                        var tmp2 = tmp1.Where(x => x.Title.Contains(Country, StringComparison.OrdinalIgnoreCase));
                                        //Find correct session episode
                                        IEnumerable<EpisodeResource> tmp3 = GetEpisodesByShowType(tmp2, seriesInfo.Title, ShowType);

                                        var Episode = tmp3.FirstOrDefault();

                                        if (Episode != null)
                                        {
                                            var Quality = Regex.Match(ReleasePost.Title, @"(2160[Pp]|4[Kk]|1080[Pp]|720[Pp]|480[Pp]|240[Pp])", RegexOptions.IgnoreCase);

                                            var SeriesMap = await _seriesApi.ApiV3SeriesIdGetAsync(Episode.SeriesId);

                                            if (SeriesMap != null)
                                            {
                                                var SceneMapping = new AlternateTitleResource() { Title = Episode.Title, SeasonNumber = Episode.SeasonNumber, SceneSeasonNumber = Episode.SceneSeasonNumber };

                                                ReleasePost.SceneMapping = SceneMapping;
                                                ReleasePost.TvdbId = SeriesMap.TvdbId;
                                                ReleasePost.Title = $"{SeriesMap.Title} - S{Episode.SeasonNumber}E{string.Format("{0:00}", Episode.EpisodeNumber)} - {Episode.Title} {Quality}";
                                                ReleasePost.SeriesId = SeriesMap.Id;
                                                ReleasePost.SeasonNumber = Episode.SeasonNumber;
                                                ReleasePost.EpisodeNumbers = new List<int?>() { Episode.EpisodeNumber };
                                            }
                                        }
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
                                    foreach (var r in response)
                                    {
                                        if (r.Rejected == false)
                                        {
                                            _hashes.Add(r);
                                            if (enableHardlinking && !_timer.Enabled)
                                            {
                                                _timer.Start();
                                                Console.WriteLine("[Hardlinking] Release queued — starting download monitor.");
                                            }
                                        }
                                    }
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
        private static async void _checkEvents(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (!running)
            {
                if (_hashes.IsEmpty) { running = false; return; }
                running = true;
                ////
                ///Used to monitor qBit and then Hardlink once the download is complete 
                ///and will also trigger a media import og the torrent folder in Sonarr.
                ////
                ///

                foreach (var r in _hashes.ToList())
                {
                    if (r.InfoHash == null)
                    {
                        var history = await _historyApi!.ApiV3HistoryGetAsync(null, true);

                        foreach (var h in history.Records.Where(x => x.SourceTitle == r.Title && x.Date < DateTime.Now.AddMinutes(-1)))
                        {
                            if (h.EventType == EpisodeHistoryEventType.Grabbed)
                            {
                                Console.WriteLine($"Added Grabbed Torrent from Sonarr history {h.DownloadId}");
                                r.InfoHash = h.DownloadId.ToLower();
                            }
                        }
                        await Task.Delay(1000);
                    }

                    if (r.InfoHash != null)
                    {
                        try
                        {
                            var query = new TorrentListQuery() { Hashes = new string[] { r.InfoHash } };
                            var result = await _qBittorrentClient!.GetTorrentListAsync(query);

                            if (result.Count > 0)
                            {
                                var torrent = result.FirstOrDefault();
                                if (torrent != null && torrent.CompletionOn != null)
                                {
                                    var sonarrItem = _hashes.Where(x => x.InfoHash == torrent.Hash).FirstOrDefault();
                                    if (sonarrItem != null)
                                    {
                                        FileAttributes attr = File.GetAttributes(Path.Combine(torrent.SavePath!, torrent.Name!));
                                        var hardpathcomplete = Path.Combine(Hardlinkpath!, sonarrItem.Title!);

                                        Directory.CreateDirectory(hardpathcomplete);

                                        if ((attr & FileAttributes.Directory) == FileAttributes.Directory)
                                        {
                                            var files = Directory.GetFiles(Path.Combine(torrent.SavePath!, torrent.Name!));

                                            //Attempt to Hardlink files.
                                            foreach (var file in files)
                                            {
                                                var ofInfo = new FileInfo(file);
                                                var nfInfo = new FileInfo($"{hardpathcomplete}/{sonarrItem.Title} - {ofInfo.Name}");

                                                if (ofInfo.Name.ToLower().Contains("buildup"))
                                                {
                                                    nfInfo = new FileInfo($"{hardpathcomplete}/{sonarrItem.Title} - Part1{ofInfo.Extension}");

                                                    Console.WriteLine($"Hard Linking {ofInfo.Name} to {nfInfo.Name}");
                                                    int linkResult = HardLink(ofInfo.ToString(), nfInfo.ToString());
                                                    if (linkResult != 0) Console.WriteLine($"Hard link failed (code {linkResult}): {ofInfo.Name}");
                                                }
                                                else if (ofInfo.Name.ToLower().Contains("session"))
                                                {
                                                    nfInfo = new FileInfo($"{hardpathcomplete}/{sonarrItem.Title} - Part2{ofInfo.Extension}");

                                                    Console.WriteLine($"Hard Linking {ofInfo.Name} to {nfInfo.Name}");
                                                    int linkResult = HardLink(ofInfo.ToString(), nfInfo.ToString());
                                                    if (linkResult != 0) Console.WriteLine($"Hard link failed (code {linkResult}): {ofInfo.Name}");
                                                }
                                                else if (ofInfo.Name.ToLower().Contains("analysis"))
                                                {
                                                    nfInfo = new FileInfo($"{hardpathcomplete}/{sonarrItem.Title} - Part3{ofInfo.Extension}");

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

                                            var commandResource = new CommandResource
                                            {
                                                Name = "DownloadedEpisodesScan",
                                                Path = hardpathcomplete,
                                                ImportMode = CommandResource.ImportModeEnum.Auto
                                            };

                                            await _commandApi!.ApiV3CommandPostAsync(commandResource);

                                            Console.WriteLine($"Sending Command:{commandResource.Name} Mode:{commandResource.ImportMode} Torrent:{torrent.Name} for path \"{commandResource.Path}\"");
                                        }
                                        else
                                        {
                                            var file = Path.Combine(torrent.SavePath!, torrent.Name!);
                                            var ofInfo = new FileInfo(file);
                                            var nameLower = ofInfo.Name.ToLower();

                                            FileInfo nfInfo;
                                            if (nameLower.Contains("buildup"))
                                                nfInfo = new FileInfo($"{hardpathcomplete}/{sonarrItem.Title} - Part1{ofInfo.Extension}");
                                            else if (nameLower.Contains("session"))
                                                nfInfo = new FileInfo($"{hardpathcomplete}/{sonarrItem.Title} - Part2{ofInfo.Extension}");
                                            else if (nameLower.Contains("analysis"))
                                                nfInfo = new FileInfo($"{hardpathcomplete}/{sonarrItem.Title} - Part3{ofInfo.Extension}");
                                            else
                                                nfInfo = new FileInfo($"{hardpathcomplete}/{sonarrItem.Title}{ofInfo.Extension}");

                                            if (!File.Exists(nfInfo.ToString()))
                                            {
                                                Console.WriteLine($"Hard Linking File {ofInfo.Name} to {nfInfo.Name}");
                                                int linkResult = HardLink(ofInfo.ToString(), nfInfo.ToString());
                                                if (linkResult != 0) Console.WriteLine($"Hard link failed (code {linkResult}): {ofInfo.Name}");
                                            }

                                            var commandResource = new CommandResource
                                            {
                                                Name = "DownloadedEpisodesScan",
                                                Path = hardpathcomplete,
                                                ImportMode = CommandResource.ImportModeEnum.Auto
                                            };

                                            await _commandApi!.ApiV3CommandPostAsync(commandResource);

                                            Console.WriteLine($"Sending Command:{commandResource.Name} Mode:{commandResource.ImportMode} Torrent:{torrent.Name} for path \"{commandResource.Path}\"");
                                        }

                                        _hashes = new ConcurrentBag<ReleaseResource>(_hashes.Except(new[] { r }));
                                        if (_hashes.IsEmpty) Console.WriteLine("[Hardlinking] Queue empty — monitor idle.");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Attempting Simple Reauth to torrent Client");

                            await _qBittorrentClient!.LoginAsync(qBitUsername, qBitPassword);

                            Console.WriteLine(ex.ToString());
                            if (bugsnagEnabled)
                            {
                                _bugsnag?.Notify(ex);
                            }
                        }
                    }
                }
                running = false;
                if (!_hashes.IsEmpty)
                    _timer.Start();
            }
        }

    }
}