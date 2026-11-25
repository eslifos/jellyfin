using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Emby.Server.Implementations.MusicHub;

/// <summary>
/// Compares the names of artists and albums as they are written in different catalogs.
/// </summary>
internal static partial class MusicNames
{
    private const int YearTolerance = 1;
    private const int OldestYear = 1900;

    public static string CleanTitle(string title)
    {
        var clean = BracketsRegex().Replace(title, " ");
        clean = EditionSuffixRegex().Replace(clean, string.Empty).Trim();
        return clean.Length > 0 ? clean : title;
    }

    public static string Comparable(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>Same title, or the same one without notes such as "(Deluxe)". An exact title is worth 2.</summary>
    /// <param name="wanted">The title in the library.</param>
    /// <param name="other">The title in the catalog.</param>
    /// <returns>0 when they are different titles.</returns>
    public static int TitleScore(string wanted, string? other)
    {
        if (string.IsNullOrEmpty(other))
        {
            return 0;
        }

        if (Comparable(wanted) == Comparable(other))
        {
            return 2;
        }

        var clean = Comparable(CleanTitle(wanted));
        return clean.Length > 0 && clean == Comparable(CleanTitle(other)) ? 1 : 0;
    }

    public static bool IsValidYear(int year) => year >= OldestYear && year <= DateTime.UtcNow.Year + 1;

    public static DateTime? ParseDate(string? value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            && IsValidYear(date.Year))
        {
            return date;
        }

        return null;
    }

    /// <summary>When the year of the album is known, another edition from years away is not the same album.</summary>
    /// <param name="year">The year in the library.</param>
    /// <param name="found">The year in the catalog.</param>
    /// <returns>Whether they can be the same album.</returns>
    public static bool FitsYear(int? year, int? found)
        => !year.HasValue || (found.HasValue && Math.Abs(found.Value - year.Value) <= YearTolerance);

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex BracketsRegex();

    [GeneratedRegex(@"\s+-\s+(single|ep|.*\b(remaster(ed)?|version|versión|deluxe|edition|edición|expanded|bonus)\b.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex EditionSuffixRegex();
}
