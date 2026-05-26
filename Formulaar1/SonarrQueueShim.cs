using Newtonsoft.Json;

namespace Formulaar1
{
    /// <summary>
    /// Minimal direct-HTTP access to Sonarr's <c>/api/v3/queue</c> endpoint.
    /// Same shim treatment as <see cref="SonarrSeriesShim"/> and
    /// <see cref="SonarrHistoryShim"/>: queue records include nested episode
    /// and series resources, which the abandoned APIv3SonarrDotcore client
    /// fails to deserialize because of Sonarr v4's <c>"clearlogo"</c>
    /// <c>MediaCoverTypes</c> value.
    ///
    /// Used by Formulaar1's hardlink monitor to remove the stale queue
    /// entry left behind when Sonarr's Completed Download Handler races
    /// against our DownloadedEpisodesScan command -- CDH tries to import
    /// the original qBit-downloaded filename (no SxxExx, fails to parse),
    /// the scan command imports the hardlinked file with SxxExx (succeeds),
    /// and Sonarr's queue is left holding a 'Waiting to Import / Invalid
    /// season or episode' entry for a file that's already imported.
    /// </summary>
    internal static class SonarrQueueShim
    {
        public sealed class MinimalQueueItem
        {
            public int? Id { get; set; }
            public string? DownloadId { get; set; }
            public string? Title { get; set; }
            public string? Status { get; set; }
            // Added in fix19 to support conditional cleanup: only DELETE entries
            // Sonarr has flagged as stuck/errored, leave healthy entries for
            // Sonarr to manage on its own lifecycle.
            public string? TrackedDownloadStatus { get; set; }
            public string? TrackedDownloadState { get; set; }
            public string? ErrorMessage { get; set; }
        }

        public sealed class MinimalQueuePage
        {
            public List<MinimalQueueItem> Records { get; set; } = new();
        }

        public static async Task<List<MinimalQueueItem>> GetByDownloadIdAsync(
            HttpClient http, string basePath, string apiKey, string downloadId)
        {
            // pageSize=200 covers a deep queue without paginating; includeUnknownSeriesItems
            // so we still find entries Sonarr couldn't match to a series.
            var url = $"{basePath.TrimEnd('/')}/api/v3/queue?page=1&pageSize=200&includeUnknownSeriesItems=true";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            var page = JsonConvert.DeserializeObject<MinimalQueuePage>(json) ?? new MinimalQueuePage();
            return page.Records
                .Where(x => !string.IsNullOrEmpty(x.DownloadId) &&
                            string.Equals(x.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public static async Task DeleteAsync(
            HttpClient http, string basePath, string apiKey, int queueId,
            bool removeFromClient = false, bool blocklist = false)
        {
            // removeFromClient=false keeps the qBit torrent in place so seeding
            // continues; we just want Sonarr to forget the queue tracking entry.
            var url = $"{basePath.TrimEnd('/')}/api/v3/queue/{queueId}?removeFromClient={removeFromClient.ToString().ToLower()}&blocklist={blocklist.ToString().ToLower()}";
            using var req = new HttpRequestMessage(HttpMethod.Delete, url);
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
        }
    }
}
