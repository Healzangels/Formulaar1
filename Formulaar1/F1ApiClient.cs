using System.Text.Json;
using System.Text.Json.Nodes;

namespace Formulaar1
{
    internal static class F1ApiClient
    {
        private const string CurrentSeasonUrl = "https://f1api.dev/api/current";

        /// <summary>
        /// Fetches the current F1 season from f1api.dev and returns a dictionary of
        /// circuit.country and circuit.city → circuit.country mappings to supplement
        /// the static Countries dictionary. Returns an empty dictionary on failure.
        /// </summary>
        internal static async Task<Dictionary<string, string>> FetchCircuitCountriesAsync(HttpClient httpClient)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var json = await httpClient.GetStringAsync(CurrentSeasonUrl);
                var root = JsonNode.Parse(json);
                var races = root?["races"]?.AsArray();
                if (races == null) return result;

                foreach (var race in races)
                {
                    var circuit = race?["circuit"];
                    var country = circuit?["country"]?.GetValue<string>();
                    var city = circuit?["city"]?.GetValue<string>();

                    if (!string.IsNullOrWhiteSpace(country))
                    {
                        result.TryAdd(country, country);
                        if (!string.IsNullOrWhiteSpace(city))
                            result.TryAdd(city, country);
                    }
                }

                Console.WriteLine($"[F1API] Loaded {races.Count} circuits for current season.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[F1API] Could not fetch circuit data (using static dictionary only): {ex.Message}");
            }

            return result;
        }
    }
}
