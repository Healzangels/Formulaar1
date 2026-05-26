using APIv3SonarrDotcore.Model;
using System.Text.RegularExpressions;

namespace Formulaar1
{
    internal class Helpers
    {
        private static readonly Regex _showTypeRegex = new Regex(
            @"Sprint\s+Shootout|Sprint\s+Qualifying|Pre\s+Qualifying\s+Show|Feature\s+Race|Sprint\s+Race|Shootout|Sprint|Feature\s+Race|Qualifying|Qually|Qualy|Race|(?:Practice|Practise)\s*(?:One|Two|Three|[1-3])|FP\s*[1-3]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Returns a canonical ShowType string, or null if the session should be dropped.
        /// </summary>
        internal static string? NormaliseShowType(string normalisedTitle)
        {
            var m = _showTypeRegex.Match(normalisedTitle);
            // Previously this defaulted to "Race" when no session marker was found,
            // which caused generic releases like "Formula1.2026.Canadian.Grand.Prix"
            // (e.g. BILLIE all-in-one rips) to be force-mapped to the Race episode --
            // and then to the wrong race entirely on sprint weekends, because the
            // default GetEpisodesByShowType("Race") branch matched Sprint Race too.
            // Returning null here drops the release out of the translation pipeline;
            // Program.cs forwards it to Sonarr untouched and Sonarr's own parser
            // handles it from there. Formulaar1 only translates titles whose
            // session is explicit.
            if (!m.Success) return null;

            var raw = Regex.Replace(m.Value.Trim(), @"\s+", " ");

            if (raw.Equals("Pre Qualifying Show", StringComparison.OrdinalIgnoreCase))
                return null;

            if (raw.Equals("Sprint Shootout", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("Sprint Qualifying", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("Shootout", StringComparison.OrdinalIgnoreCase))
                return "Sprint Shootout";

            if (raw.Equals("Sprint Race", StringComparison.OrdinalIgnoreCase))
                return "Sprint Race";

            if (raw.Equals("Sprint", StringComparison.OrdinalIgnoreCase))
                return "Sprint";

            if (raw.Equals("Feature Race", StringComparison.OrdinalIgnoreCase))
                return "Feature Race";

            if (raw.Equals("Qualifying", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("Qually", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("Qualy", StringComparison.OrdinalIgnoreCase))
                return "Qualifying";

            if (raw.Equals("Race", StringComparison.OrdinalIgnoreCase))
                return "Race";

            // Practice variants: normalise number word and fp prefix
            var practiceNum = Regex.Match(raw, @"(?:Practice|Practise|FP)\s*(?:(One|1)|(Two|2)|(Three|3))", RegexOptions.IgnoreCase);
            if (practiceNum.Success)
            {
                if (practiceNum.Groups[1].Success) return "Practice 1";
                if (practiceNum.Groups[2].Success) return "Practice 2";
                if (practiceNum.Groups[3].Success) return "Practice 3";
            }

            // The regex matched a session keyword but no specific branch above
            // claimed it. Return null rather than guessing "Race" so the release
            // is dropped instead of misrouted.
            return null;
        }

        /// <summary>
        /// Filters episodes by ShowType, applying series-specific logic for F1 vs F2/F3.
        /// Takes the shim's MinimalEpisode rather than the SDK's EpisodeResource so
        /// the entire pipeline runs through our shimmed deserialisers and Helpers
        /// doesn't need a reference to the abandoned APIv3SonarrDotcore client.
        /// </summary>
        internal static IEnumerable<SonarrEpisodeShim.MinimalEpisode> GetEpisodesByShowType(
            IEnumerable<SonarrEpisodeShim.MinimalEpisode> candidates, string seriesTitle, string showType)
        {
            bool isF1 = seriesTitle.Equals("Formula 1", StringComparison.OrdinalIgnoreCase);

            return showType switch
            {
                "Sprint Shootout" when isF1 =>
                    candidates.Where(x => (x.Title ?? string.Empty).Contains("Shootout", StringComparison.OrdinalIgnoreCase) ||
                                          (x.Title ?? string.Empty).Contains("Sprint Qualifying", StringComparison.OrdinalIgnoreCase)),

                "Sprint Race" when isF1 =>
                    candidates.Where(x => (x.Title ?? string.Empty).Contains("Sprint", StringComparison.OrdinalIgnoreCase) &&
                                          !(x.Title ?? string.Empty).Contains("Shootout", StringComparison.OrdinalIgnoreCase)),

                "Sprint" when isF1 =>
                    candidates.Where(x => (x.Title ?? string.Empty).Contains("Sprint", StringComparison.OrdinalIgnoreCase) &&
                                          !(x.Title ?? string.Empty).Contains("Shootout", StringComparison.OrdinalIgnoreCase)),

                // F1 "Race" must exclude Sprint Race and Feature Race episodes.
                // The default `_ =>` branch below would otherwise match "Sprint Race"
                // too (since "Sprint Race".Contains("Race") is true), which on a
                // sprint weekend pulls the lower-numbered Sprint Race episode
                // instead of the main Race.
                "Race" when isF1 =>
                    candidates.Where(x => (x.Title ?? string.Empty).Contains("Race", StringComparison.OrdinalIgnoreCase) &&
                                          !(x.Title ?? string.Empty).Contains("Sprint Race", StringComparison.OrdinalIgnoreCase) &&
                                          !(x.Title ?? string.Empty).Contains("Feature Race", StringComparison.OrdinalIgnoreCase)),

                // Same shape for Qualifying: the TVDB sprint-weekend episode list
                // contains both "Qualifying" and "Sprint Qualifying", and the default
                // Contains() match would grab the lower-numbered Sprint Qualifying.
                "Qualifying" when isF1 =>
                    candidates.Where(x => (x.Title ?? string.Empty).Contains("Qualifying", StringComparison.OrdinalIgnoreCase) &&
                                          !(x.Title ?? string.Empty).Contains("Sprint Qualifying", StringComparison.OrdinalIgnoreCase)),

                "Sprint Race" =>
                    candidates.Where(x => (x.Title ?? string.Empty).Contains("Sprint Race", StringComparison.OrdinalIgnoreCase)),

                "Sprint" =>
                    candidates.Where(x => (x.Title ?? string.Empty).Contains("Sprint Race", StringComparison.OrdinalIgnoreCase)),

                "Feature Race" =>
                    candidates.Where(x => (x.Title ?? string.Empty).Contains("Feature Race", StringComparison.OrdinalIgnoreCase)),

                "Race" when !isF1 =>
                    candidates.Where(x => (x.Title ?? string.Empty).Contains("Feature Race", StringComparison.OrdinalIgnoreCase)),

                _ =>
                    candidates.Where(x => (x.Title ?? string.Empty).Contains(showType, StringComparison.OrdinalIgnoreCase)),
            };
        }

        internal record SeriesInfo(string Title, int TvdbId);

        internal static SeriesInfo? DetectSeries(string normalisedTitle)
        {
            if (normalisedTitle.Contains("Formula 2", StringComparison.OrdinalIgnoreCase) ||
                normalisedTitle.Contains("Formula2", StringComparison.OrdinalIgnoreCase))
                return new SeriesInfo("Formula 2", 392717);

            if (normalisedTitle.Contains("Formula 3", StringComparison.OrdinalIgnoreCase) ||
                normalisedTitle.Contains("Formula3", StringComparison.OrdinalIgnoreCase))
                return new SeriesInfo("Formula 3", 396724);

            if (normalisedTitle.Contains("Formula 1", StringComparison.OrdinalIgnoreCase) ||
                normalisedTitle.Contains("Formula1", StringComparison.OrdinalIgnoreCase))
                return new SeriesInfo("Formula 1", 387219);

            return null;
        }

        internal static Dictionary<string, string> Countries = new(StringComparer.OrdinalIgnoreCase)
            {
                    { "Bahrain", "Bahrain" },
                    { "Saudi Arabia", "Saudi Arabia" },
                    { "Saudi Arabian", "Saudi Arabia" },
                    { "SaudiArabia", "Saudi Arabia" },
                    { "SaudiArabian", "Saudi Arabia" },
                    { "Australia", "Australia" },
                    { "Australian", "Australia" },
                    { "Azerbaijan", "Azerbaijan" },
                    { "Miami", "Miami" },
                    { "Emilia Romagna", "Emilia Romagna" },
                    { "EmiliaRomagna", "Emilia Romagna" },
                    { "Imola", "Emilia Romagna" },
                    { "Monaco", "Monaco" },
                    { "Spain", "Spain" },
                    { "Spanish", "Spain" },
                    { "Canada", "Canada" },
                    { "Canadian", "Canada" },
                    { "Austria", "Austria" },
                    { "Austrian", "Austria" },
                    { "Great Britain", "Great Britain" },
                    { "GreatBritain", "Great Britain" },
                    { "British", "Great Britain" },
                    { "Britain", "Great Britain" },
                    { "Hungary", "Hungary" },
                    { "Belgium", "Belgium" },
                    { "Belgian", "Belgium" },
                    { "Netherlands", "Netherlands" },
                    { "Dutch", "Netherlands" },
                    { "Italy", "Italy" },
                    { "Italian", "Italy" },
                    { "Singapore", "Singapore" },
                    { "Japan", "Japan" },
                    { "Japanese", "Japan" },
                    { "Qatar", "Qatar" },
                    { "United States", "United States" },
                    { "UnitedStates", "United States" },
                    { "USA", "United States" },
                    { "COTA", "United States" },
                    { "Austin", "United States" },
                    { "Mexico", "Mexico" },
                    { "Mexican", "Mexico" },
                    { "Brazil", "Brazil" },
                    { "Brazilian", "Brazil" },
                    { "Las Vegas", "Las Vegas" },
                    { "LasVegas", "Las Vegas" },
                    { "Abu Dhabi", "Abu Dhabi" },
                    { "AbuDhabi", "Abu Dhabi" },
                    { "UAE", "Abu Dhabi" },
                    { "UnitedArabEmirates", "Abu Dhabi" },
                    { "United Arab Emirates", "Abu Dhabi" },
        };

        /// <summary>
        /// Merges circuit data fetched from f1api.dev into the Countries dictionary.
        /// Static entries (including aliases) take priority and are never overwritten.
        /// </summary>
        internal static void MergeCircuitCountries(Dictionary<string, string> apiCountries)
        {
            foreach (var kvp in apiCountries)
                Countries.TryAdd(kvp.Key, kvp.Value);
        }
    }
}
