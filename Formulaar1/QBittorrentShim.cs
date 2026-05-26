using Newtonsoft.Json;

namespace Formulaar1
{
    /// <summary>
    /// Direct HTTP calls to qBittorrent's Web API that bypass the bundled
    /// <c>QBittorrent.Client</c> NuGet client (last-updated 2023, version
    /// 1.8.23016.2).
    ///
    /// <para>
    /// Why: the bundled client deserialises <c>torrent.state</c> into a
    /// strict <c>TorrentState</c> enum. qBittorrent 5.0 renamed the pause
    /// states (<c>pausedDL</c>/<c>pausedUP</c> became <c>stoppedDL</c>/
    /// <c>stoppedUP</c>), and the bundled client's enum doesn't know about
    /// the new names. Result: <c>JsonSerializationException</c> in our
    /// hardlink monitor on every tick the user pauses a torrent, and a
    /// similar break for any future qBit state additions. This shim treats
    /// <c>state</c> as a raw string, so we're immune to enum churn.
    /// </para>
    /// <para>
    /// Same minimal-POCO pattern as the Sonarr shims: we only model the
    /// fields the monitor actually reads (<c>Hash</c>, <c>Name</c>,
    /// <c>SavePath</c>, <c>CompletionOn</c>). Authentication is the qBit
    /// "Web UI" cookie flow: POST to /api/v2/auth/login with form-encoded
    /// username+password, server returns a <c>Set-Cookie: SID=...</c>
    /// header, subsequent requests include that cookie.
    /// </para>
    /// </summary>
    internal static class QBittorrentShim
    {
        public sealed class MinimalTorrent
        {
            public string? Hash { get; set; }
            public string? Name { get; set; }
            // Kept as a raw string -- we only use it for diagnostic logging,
            // never enum-compared. Sidesteps the entire SDK breakage class.
            public string? State { get; set; }
            [JsonProperty("save_path")]
            public string? SavePath { get; set; }
            [JsonProperty("completion_on")]
            public long CompletionOnUnix { get; set; }

            /// <summary>
            /// qBit returns completion_on as a Unix timestamp (seconds), or
            /// 0 if not yet complete. Convert to a nullable DateTime for the
            /// monitor's null-vs-completed check pattern.
            /// </summary>
            public DateTime? CompletionOn =>
                CompletionOnUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(CompletionOnUnix).UtcDateTime
                    : (DateTime?)null;
        }

        /// <summary>
        /// POST /api/v2/auth/login. Returns the full <c>name=value</c> cookie
        /// pair (ready to drop into a <c>Cookie:</c> request header) on
        /// success, null on failure. Body of a successful response is the
        /// literal text "Ok."; failure is "Fails."; HTTP status is 200 either
        /// way, so we can't trust the status code alone.
        ///
        /// <para>
        /// Cookie name handling (fix21): qBit's session cookie name depends on
        /// version. Older builds used <c>SID</c>; current builds (4.x WebUI
        /// rewrite onward, definitely 5.x) namespace by WebUI port and emit
        /// <c>QBT_SID_&lt;port&gt;</c> (e.g. <c>QBT_SID_8080</c>) so multiple
        /// qBit instances on the same host don't fight over a shared cookie
        /// jar. Match both, and persist the full <c>name=value</c> pair so
        /// callers don't have to know which form they got back.
        /// </para>
        /// </summary>
        public static async Task<string?> LoginAsync(
            HttpClient http, string baseUrl, string username, string password)
        {
            var url = $"{baseUrl.TrimEnd('/')}/api/v2/auth/login";
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", username),
                new KeyValuePair<string, string>("password", password),
            });
            // Referer is required by qBit for CSRF protection on /api/v2/auth/login.
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            req.Headers.Referrer = new Uri(baseUrl);
            using var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            // Diagnostic logging so we can SEE why login failed when it does.
            // qBit's auth conventions: 200 OK with body "Ok." on success, body
            // "Fails." on bad credentials, 403 Forbidden if the user/IP is
            // banned. Some configurations also require Referer matching.
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[QBit] Login refused: HTTP {(int)resp.StatusCode} {resp.StatusCode} -- body='{Truncate(body, 200)}'");
                return null;
            }
            if (body.Contains("Fails", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[QBit] Login failed (bad credentials?): body='{Truncate(body, 200)}'");
                return null;
            }

            if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var cookie in cookies)
                {
                    // Accept both legacy 'SID=' and current 'QBT_SID_<port>='
                    // names. The Set-Cookie line looks like:
                    //   QBT_SID_8080=abc123...; HttpOnly; path=/
                    // We want the leading 'name=value' segment, dropping any
                    // attributes after the first semicolon.
                    if (cookie.StartsWith("SID=", StringComparison.Ordinal) ||
                        cookie.StartsWith("QBT_SID_", StringComparison.Ordinal))
                    {
                        var semi = cookie.IndexOf(';');
                        var pair = semi > 0 ? cookie.Substring(0, semi) : cookie;
                        var eq = pair.IndexOf('=');
                        var name = eq > 0 ? pair.Substring(0, eq) : pair;
                        var valLen = eq > 0 ? pair.Length - eq - 1 : 0;
                        Console.WriteLine($"[QBit] Login OK, got session cookie '{name}' ({valLen} chars)");
                        return pair;
                    }
                }
                Console.WriteLine($"[QBit] Login response had Set-Cookie headers but none matched 'SID=' or 'QBT_SID_'. Cookies: {string.Join(" | ", cookies)}");
            }
            else
            {
                Console.WriteLine($"[QBit] Login succeeded (HTTP 200, body='{Truncate(body, 100)}') but NO Set-Cookie header in response. This usually means HttpClient is configured with UseCookies=true and consuming the cookie -- check the handler config.");
            }
            return null;
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max) + "...";

        /// <summary>
        /// GET /api/v2/app/version. Returns the qBit version string, or null
        /// if the request fails. Used as a sanity check after login.
        ///
        /// <para>
        /// <paramref name="cookie"/> is the full <c>name=value</c> pair
        /// returned by <see cref="LoginAsync"/> -- it's already in
        /// <c>Cookie:</c>-header form, so we just shove it in verbatim.
        /// </para>
        /// </summary>
        public static async Task<string?> GetVersionAsync(
            HttpClient http, string baseUrl, string cookie)
        {
            var url = $"{baseUrl.TrimEnd('/')}/api/v2/app/version";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Cookie", cookie);
            using var resp = await http.SendAsync(req);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : null;
        }

        /// <summary>
        /// GET /api/v2/torrents/info?hashes=H. Returns torrents matching the
        /// given hash. qBit accepts multiple hashes separated by '|', but
        /// the monitor only ever looks up one at a time.
        ///
        /// <para>
        /// <paramref name="cookie"/> is the full <c>name=value</c> pair
        /// returned by <see cref="LoginAsync"/>.
        /// </para>
        /// </summary>
        public static async Task<List<MinimalTorrent>> GetByHashAsync(
            HttpClient http, string baseUrl, string cookie, string hash)
        {
            var url = $"{baseUrl.TrimEnd('/')}/api/v2/torrents/info?hashes={Uri.EscapeDataString(hash)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Cookie", cookie);
            using var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<List<MinimalTorrent>>(json)
                   ?? new List<MinimalTorrent>();
        }
    }
}
