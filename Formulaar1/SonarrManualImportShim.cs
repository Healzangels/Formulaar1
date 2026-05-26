using Newtonsoft.Json.Linq;

namespace Formulaar1
{
    /// <summary>
    /// Minimal direct-HTTP access to Sonarr's <c>/api/v3/manualimport</c>
    /// endpoint. Same shim treatment as the series/history/queue shims --
    /// the bundled <c>APIv3SonarrDotcore</c> client doesn't expose this
    /// endpoint, and even if it did the deeply-nested response would trip
    /// the <c>MediaCoverTypes</c> deserialiser.
    ///
    /// <para>
    /// Why we use this instead of <c>DownloadedEpisodesScan</c>:
    /// </para>
    /// <para>
    /// The scan command imports files from a folder but doesn't link the
    /// resulting import back to the queue entry it came from. Sonarr's
    /// Completed Download Handler then keeps the queue tracking entry in a
    /// stuck 'Waiting to Import / Invalid season or episode' state because
    /// it never sees a matching import event. The fix12 workaround was to
    /// DELETE the queue entry after a 5s sleep.
    /// </para>
    /// <para>
    /// <c>manualimport</c> instead accepts <c>downloadId</c> on each item
    /// (this is what the Manual Import dialog in the Sonarr UI uses) and
    /// links the import to the queue entry atomically -- the queue clears
    /// as a side effect of the import, no separate cleanup step needed.
    /// </para>
    /// <para>
    /// We treat the response items as opaque <see cref="JObject"/> so we
    /// don't have to model Sonarr's full ManualImportResource shape; we
    /// just trust the GET output (with downloadId added) and POST it back.
    /// </para>
    /// </summary>
    internal static class SonarrManualImportShim
    {
        // Note on downloadId: we deliberately do NOT pass it in the GET query.
        // Empirical testing (fix15-era diagnostic curls against Sonarr's API)
        // showed that GET /api/v3/manualimport?downloadId=X filters to 0
        // results when the folder is anything other than the qBit download
        // path -- Sonarr's logic appears to be "show me files belonging to
        // download X in this folder," and our hardlink staging dir isn't
        // qBit's path, so nothing matches. Without downloadId in the GET,
        // Sonarr does a plain folder scan and returns the hardlinked file
        // as importable. The caller still injects downloadId on each item
        // before CommitAsync so the POST links the import to the queue
        // entry atomically -- best of both worlds.
        public static async Task<List<JObject>> GetSuggestionsAsync(
            HttpClient http, string basePath, string apiKey, string folder)
        {
            var url = $"{basePath.TrimEnd('/')}/api/v3/manualimport" +
                      $"?folder={Uri.EscapeDataString(folder)}" +
                      $"&filterExistingFiles=true";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            var arr = JArray.Parse(json);
            return arr.OfType<JObject>().ToList();
        }

        /// <summary>
        /// POSTs the given items back to Sonarr's manualimport endpoint.
        /// Returns (success, responseBody) so the caller can log the
        /// server's explanation on non-2xx responses.
        /// </summary>
        public static async Task<(bool Success, string Detail)> CommitAsync(
            HttpClient http, string basePath, string apiKey, List<JObject> items)
        {
            var body = new JArray(items.ToArray()).ToString();
            var url = $"{basePath.TrimEnd('/')}/api/v3/manualimport";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("X-Api-Key", apiKey);
            req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            var respBody = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, respBody);
        }
    }
}
