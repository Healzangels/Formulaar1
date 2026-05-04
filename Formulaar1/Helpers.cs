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
            if (!m.Success) return "Race";

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

            return "Race";
        }

        /// <summary>
        /// Filters episodes by ShowType, applying series-specific logic for F1 vs F2/F3.
        /// </summary>
        internal static IEnumerable<EpisodeResource> GetEpisodesByShowType(
            IEnumerable<EpisodeResource> candidates, string seriesTitle, string showType)
        {
            bool isF1 = seriesTitle.Equals("Formula 1", StringComparison.OrdinalIgnoreCase);

            return showType switch
            {
                "Sprint Shootout" when isF1 =>
                    candidates.Where(x => x.Title.Contains("Shootout", StringComparison.OrdinalIgnoreCase) ||
                                          x.Title.Contains("Sprint Qualifying", StringComparison.OrdinalIgnoreCase)),

                "Sprint Race" when isF1 =>
                    candidates.Where(x => x.Title.Contains("Sprint", StringComparison.OrdinalIgnoreCase) &&
                                          !x.Title.Contains("Shootout", StringComparison.OrdinalIgnoreCase)),

                "Sprint" when isF1 =>
                    candidates.Where(x => x.Title.Contains("Sprint", StringComparison.OrdinalIgnoreCase) &&
                                          !x.Title.Contains("Shootout", StringComparison.OrdinalIgnoreCase)),

                "Sprint Race" =>
                    candidates.Where(x => x.Title.Contains("Sprint Race", StringComparison.OrdinalIgnoreCase)),

                "Sprint" =>
                    candidates.Where(x => x.Title.Contains("Sprint Race", StringComparison.OrdinalIgnoreCase)),

                "Feature Race" =>
                    candidates.Where(x => x.Title.Contains("Feature Race", StringComparison.OrdinalIgnoreCase)),

                "Race" when !isF1 =>
                    candidates.Where(x => x.Title.Contains("Feature Race", StringComparison.OrdinalIgnoreCase)),

                _ =>
                    candidates.Where(x => x.Title.Contains(showType, StringComparison.OrdinalIgnoreCase)),
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
