using System.IO;
using System.Text.RegularExpressions;
using Emby.Naming.Common;

namespace Emby.Naming.TV
{
    /// <summary>
    /// Used to resolve information about series from path.
    /// </summary>
    public static partial class SeriesResolver
    {
        /// <summary>
        /// Regex that matches a run of dots or underscores that separates two words, where a word is
        /// at least 2 characters long. Used for removing separators between words, i.e turns
        /// "The_show" into "The show" while preserving acronyms like "S.H.O.W", whose single letters
        /// are a word on neither side. Whitespace bounds a word too, so the dot in
        /// "Marvel's Agents of S.H.I.E.L.D." is read against the "S" beside it rather than against
        /// the whole run of words before it.
        /// </summary>
        [GeneratedRegex(@"(?<=[^\s\._]{2})[\._]+|[\._]+(?=[^\s\._]{2})")]
        private static partial Regex SeriesNameRegex();

        /// <summary>
        /// Regex that matches titles with year in parentheses. Captures the title (which may be
        /// numeric) before the year, i.e. turns "1923 (2022)" into "1923".
        /// </summary>
        [GeneratedRegex(@"(?<title>.+?)\s*\((?<year>[0-9]{4})\)")]
        private static partial Regex TitleWithYearRegex();

        /// <summary>
        /// Regex that matches provider id tags such as "{tvdb-339632}" or "[imdbid-tt7909708]", which are
        /// read separately and are not part of the name.
        /// </summary>
        [GeneratedRegex(@"\s*[\{\[](?:tmdb|tvdb|imdb|tvmaze|anidb|anilist|anisearch)(?:id)?[-=][^\}\]]*[\}\]]", RegexOptions.IgnoreCase)]
        private static partial Regex ProviderIdTagRegex();

        /// <summary>
        /// Resolve information about series from path.
        /// </summary>
        /// <param name="options"><see cref="NamingOptions"/> object passed to <see cref="SeriesPathParser"/>.</param>
        /// <param name="path">Path to series.</param>
        /// <returns>SeriesInfo.</returns>
        public static SeriesInfo Resolve(NamingOptions options, string path)
        {
            string seriesName = ProviderIdTagRegex().Replace(Path.GetFileName(path), string.Empty).Trim();

            // First check if the filename matches a title with year pattern (handles numeric titles)
            if (!string.IsNullOrEmpty(seriesName))
            {
                var titleWithYearMatch = TitleWithYearRegex().Match(seriesName);
                if (titleWithYearMatch.Success)
                {
                    seriesName = titleWithYearMatch.Groups["title"].Value.Trim();
                    return new SeriesInfo(path)
                    {
                        Name = seriesName,
                        Year = int.TryParse(titleWithYearMatch.Groups["year"].ValueSpan, out var year) ? year : null
                    };
                }
            }

            SeriesPathParserResult result = SeriesPathParser.Parse(options, path);
            if (result.Success)
            {
                if (!string.IsNullOrEmpty(result.SeriesName))
                {
                    seriesName = result.SeriesName;
                }
            }

            if (!string.IsNullOrEmpty(seriesName))
            {
                seriesName = ProviderIdTagRegex().Replace(seriesName, string.Empty);
                seriesName = SeriesNameRegex().Replace(seriesName, " ").Trim();
            }

            return new SeriesInfo(path)
            {
                Name = seriesName
            };
        }
    }
}
