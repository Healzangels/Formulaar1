using Newtonsoft.Json;

namespace Formulaar1
{
    /// <summary>
    /// Direct HTTP calls to Sonarr's /api/v3/series endpoints that bypass the
    /// bundled <c>APIv3SonarrDotcore</c> deserializer.
    ///
    /// Why: the bundled client (NuGet 0.0.0.3, last released March 2023,
    /// unmaintained) has a closed <c>MediaCoverTypes</c> enum that throws on
    /// any value it doesn't know -- notably <c>"clearlogo"</c>, which Sonarr
    /// v4 returns in <c>SeriesResource.images[].coverType</c>. The throw
    /// happens during response deserialization, before the caller ever sees
    /// a result, so every release push crashes with a
    /// <c>System.UriFormatException</c>-style chain ending in
    /// <c>JsonReaderException</c> at <c>ApiClient.Deserialize</c>.
    ///
    /// This shim deserializes only the fields Formulaar1 actually uses
    /// (<c>Id</c>, <c>Title</c>, <c>TvdbId</c>) and ignores the
    /// <c>images</c> array entirely, so it is resilient to any future
    /// additions to the Sonarr schema.
    /// </summary>
    internal static class SonarrSeriesShim
    {
        public sealed class MinimalSeries
        {
            public int? Id { get; set; }
            public string? Title { get; set; }
            public int? TvdbId { get; set; }
        }

        public static async Task<List<MinimalSeries>> GetByTvdbIdAsync(
            HttpClient http, string basePath, string apiKey, int? tvdbId)
        {
            var url = $"{basePath.TrimEnd('/')}/api/v3/series?tvdbId={tvdbId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<List<MinimalSeries>>(json)
                   ?? new List<MinimalSeries>();
        }

        public static async Task<MinimalSeries?> GetByIdAsync(
            HttpClient http, string basePath, string apiKey, int? seriesId)
        {
            var url = $"{basePath.TrimEnd('/')}/api/v3/series/{seriesId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<MinimalSeries>(json);
        }
    }
}
