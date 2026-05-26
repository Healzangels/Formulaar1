using Newtonsoft.Json;

namespace Formulaar1
{
    /// <summary>
    /// Direct HTTP call to Sonarr's <c>/api/v3/episode</c> endpoint that
    /// bypasses the bundled <c>APIv3SonarrDotcore</c> deserializer.
    ///
    /// <para>
    /// Why preemptive: <c>EpisodeResource</c> in current Sonarr v4 is
    /// mostly flat scalars and doesn't trip the <c>MediaCoverTypes</c>
    /// "clearlogo" issue today, so the original <c>_episodeApi</c> call
    /// has been working. But it's the one Sonarr API we hadn't shimmed --
    /// if Sonarr ever nests series/cover data into episode responses
    /// (Sonarr v5? a v4.x change?), we'd hit the same crash inside the
    /// release-push handler where it's harder to recover from than the
    /// timer loop. Shimming preemptively eliminates the last surface area
    /// the abandoned SDK still owns for our hot path.
    /// </para>
    /// <para>
    /// Mirrors the minimal-POCO pattern of the other shims; only the
    /// fields the release-push handler actually reads are modeled.
    /// </para>
    /// </summary>
    internal static class SonarrEpisodeShim
    {
        public sealed class MinimalEpisode
        {
            public int? Id { get; set; }
            public int? SeriesId { get; set; }
            public int? SeasonNumber { get; set; }
            public int? EpisodeNumber { get; set; }
            public int? SceneSeasonNumber { get; set; }
            public string? Title { get; set; }
        }

        public static async Task<List<MinimalEpisode>> GetBySeriesIdAsync(
            HttpClient http, string basePath, string apiKey, int? seriesId)
        {
            var url = $"{basePath.TrimEnd('/')}/api/v3/episode?seriesId={seriesId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<List<MinimalEpisode>>(json)
                   ?? new List<MinimalEpisode>();
        }
    }
}
