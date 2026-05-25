using Newtonsoft.Json;

namespace Formulaar1
{
    /// <summary>
    /// Direct HTTP call to Sonarr's <c>/api/v3/history</c> that bypasses the
    /// bundled <c>APIv3SonarrDotcore</c> deserializer.
    ///
    /// Same root cause as <see cref="SonarrSeriesShim"/>: the unmaintained
    /// SDK (NuGet 0.0.0.3, March 2023) has a closed <c>MediaCoverTypes</c>
    /// enum that throws on Sonarr v4's <c>"clearlogo"</c>. History records
    /// contain nested episode/series resources whose <c>images[].coverType</c>
    /// trips the same error.
    ///
    /// In Formulaar1 the breakage is silent: the hardlink monitor's
    /// <c>_checkEvents</c> handler calls the history endpoint to resolve a
    /// release's qBit InfoHash. The deserialize exception bubbles out of the
    /// <c>async void</c> handler with no surrounding try/catch, the timer
    /// stops rescheduling, and no hardlink ever runs -- the container logs
    /// just show <c>[Hardlinking] Release queued -- starting download monitor.</c>
    /// followed by nothing.
    ///
    /// This shim deserializes only the four fields Formulaar1 reads
    /// (<c>downloadId</c>, <c>sourceTitle</c>, <c>eventType</c>, <c>date</c>),
    /// ignoring the nested resources entirely.
    /// </summary>
    internal static class SonarrHistoryShim
    {
        public sealed class MinimalHistoryRecord
        {
            public string? DownloadId { get; set; }
            public string? SourceTitle { get; set; }
            public string? EventType { get; set; }
            public DateTime Date { get; set; }
        }

        public sealed class MinimalHistoryPage
        {
            public List<MinimalHistoryRecord> Records { get; set; } = new();
        }

        public static async Task<MinimalHistoryPage> GetRecentAsync(
            HttpClient http, string basePath, string apiKey, int pageSize = 50)
        {
            var url = $"{basePath.TrimEnd('/')}/api/v3/history?page=1&pageSize={pageSize}&sortKey=date&sortDirection=descending";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<MinimalHistoryPage>(json)
                   ?? new MinimalHistoryPage();
        }
    }
}
