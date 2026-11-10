using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Providers;

namespace MediaBrowser.Model.Extensions
{
    /// <summary>
    /// Extension methods for <see cref="IEnumerable{T}"/>.
    /// </summary>
    public static class EnumerableExtensions
    {
        // Countries whose Spanish counts as Latin American Spanish (es-419).
        private static readonly HashSet<string> _latinAmericanRegions = new(StringComparer.OrdinalIgnoreCase)
        {
            "419", "MX", "AR", "BO", "CL", "CO", "CR", "CU", "DO", "EC", "GT", "HN", "NI", "PA", "PE", "PR", "PY", "SV", "US", "UY", "VE"
        };

        /// <summary>
        /// Orders <see cref="RemoteImageInfo"/> by requested language in descending order, then "en", then no language, over other non-matches.
        /// </summary>
        /// <param name="remoteImageInfos">The remote image infos.</param>
        /// <param name="requestedLanguage">The requested language for the images.</param>
        /// <returns>The ordered remote image infos.</returns>
        public static IEnumerable<RemoteImageInfo> OrderByLanguageDescending(this IEnumerable<RemoteImageInfo> remoteImageInfos, string requestedLanguage)
            => OrderByLanguageDescending(remoteImageInfos, requestedLanguage, null, null);

        /// <summary>
        /// Orders <see cref="RemoteImageInfo"/> by how well their language fits the requested one (see <see cref="GetLanguageScore"/>),
        /// then by rating and vote count.
        /// </summary>
        /// <param name="remoteImageInfos">The remote image infos.</param>
        /// <param name="requestedLanguage">The requested language for the images, e.g. "es-419" or "es-MX".</param>
        /// <param name="countryCode">The metadata country code, e.g. "MX".</param>
        /// <param name="originalLanguage">The original language of the item.</param>
        /// <returns>The ordered remote image infos.</returns>
        public static IEnumerable<RemoteImageInfo> OrderByLanguageDescending(
            this IEnumerable<RemoteImageInfo> remoteImageInfos,
            string requestedLanguage,
            string? countryCode,
            string? originalLanguage)
        {
            return remoteImageInfos
                .OrderByDescending(i => GetLanguageScore(i.Language, requestedLanguage, countryCode, originalLanguage))
                .ThenByDescending(i => Math.Round(i.CommunityRating ?? 0, 1))
                .ThenByDescending(i => i.VoteCount ?? 0);
        }

        /// <summary>
        /// Rates how well an image language fits the requested one. Higher is better:
        /// <list type="number">
        /// <item>100: the exact language and region (es-MX for es-MX, or es-419 / es-MX for es-419 with country MX).</item>
        /// <item>90: another variant of the same region family (es-AR or es-419 for es-MX).</item>
        /// <item>80: the language without a region ("es").</item>
        /// <item>70: the language from another region (es-ES for es-MX).</item>
        /// <item>30: English.</item>
        /// <item>20: the original language of the item.</item>
        /// <item>10: no language (textless).</item>
        /// <item>0: anything else.</item>
        /// </list>
        /// </summary>
        /// <param name="imageLanguage">The image language.</param>
        /// <param name="requestedLanguage">The requested language.</param>
        /// <param name="countryCode">The metadata country code.</param>
        /// <param name="originalLanguage">The original language of the item.</param>
        /// <returns>The score.</returns>
        public static int GetLanguageScore(string? imageLanguage, string? requestedLanguage, string? countryCode = null, string? originalLanguage = null)
        {
            if (string.IsNullOrWhiteSpace(imageLanguage))
            {
                return 10;
            }

            if (string.IsNullOrWhiteSpace(requestedLanguage))
            {
                // Default to English if no requested language is specified.
                requestedLanguage = "en";
            }

            var (reqBase, reqRegion) = Split(requestedLanguage);
            var (imgBase, imgRegion) = Split(imageLanguage);

            // es-419 + country MX means es-MX
            if (string.Equals(reqRegion, "419", StringComparison.Ordinal) && !string.IsNullOrEmpty(countryCode))
            {
                if (string.Equals(imgRegion, "419", StringComparison.Ordinal) && string.Equals(reqBase, imgBase, StringComparison.OrdinalIgnoreCase))
                {
                    return 100;
                }

                reqRegion = countryCode.ToUpperInvariant();
            }
            else if (reqRegion is null && !string.IsNullOrEmpty(countryCode))
            {
                // "es" with country MX: the MX variant fits best, but any Spanish is fine
                reqRegion = countryCode.ToUpperInvariant();
            }

            if (string.Equals(reqBase, imgBase, StringComparison.OrdinalIgnoreCase))
            {
                if (imgRegion is null)
                {
                    return string.IsNullOrEmpty(Split(requestedLanguage).Region) ? 100 : 80;
                }

                if (string.Equals(reqRegion, imgRegion, StringComparison.OrdinalIgnoreCase))
                {
                    return 100;
                }

                if (reqRegion is not null && SameFamily(reqBase, reqRegion, imgRegion))
                {
                    return 90;
                }

                return 70;
            }

            if (string.Equals(imgBase, "en", StringComparison.OrdinalIgnoreCase))
            {
                return 30;
            }

            if (!string.IsNullOrWhiteSpace(originalLanguage)
                && string.Equals(Split(originalLanguage).Base, imgBase, StringComparison.OrdinalIgnoreCase))
            {
                return 20;
            }

            return 0;
        }

        private static bool SameFamily(string language, string region1, string region2)
            => string.Equals(language, "es", StringComparison.OrdinalIgnoreCase)
               && _latinAmericanRegions.Contains(region1)
               && _latinAmericanRegions.Contains(region2);

        private static (string Base, string? Region) Split(string language)
        {
            var parts = language.Trim().Replace('_', '-').Split('-', 2);
            return (parts[0].ToLowerInvariant(), parts.Length > 1 && parts[1].Length > 0 ? parts[1].ToUpperInvariant() : null);
        }
    }
}
