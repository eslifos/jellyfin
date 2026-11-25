using System;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// The language and country a library asks its metadata in, reduced to the codes online catalogs understand.
/// </summary>
/// <param name="Language">The lowercase language code, like "fr".</param>
/// <param name="Country">The lowercase country code, like "ca".</param>
public sealed record MetadataLocale(string Language, string Country)
{
    /// <summary>The language used when none is configured or it cannot be used.</summary>
    public const string FallbackLanguage = "en";

    /// <summary>The country used when none is configured or it cannot be used.</summary>
    public const string FallbackCountry = "us";

    /// <summary>Gets a value indicating whether the language is already the fallback one.</summary>
    public bool IsFallbackLanguage => Language == FallbackLanguage;

    /// <summary>Reads the codes of the settings; anything unusable becomes English and the United States.</summary>
    /// <param name="language">A language code, which may carry a region, like "pt-BR".</param>
    /// <param name="country">A country code.</param>
    /// <returns>The locale.</returns>
    public static MetadataLocale From(string? language, string? country)
    {
        var primary = language?.Trim().Split('-', '_')[0].ToLowerInvariant() ?? string.Empty;
        var code = country?.Trim().ToLowerInvariant() ?? string.Empty;
        return new MetadataLocale(
            primary.Length is 2 or 3 && IsLetters(primary) ? (primary == "nb" ? "no" : primary) : FallbackLanguage,
            code.Length == 2 && IsLetters(code) ? code : FallbackCountry);
    }

    private static bool IsLetters(string text) => text.AsSpan().IndexOfAnyExceptInRange('a', 'z') < 0;
}
