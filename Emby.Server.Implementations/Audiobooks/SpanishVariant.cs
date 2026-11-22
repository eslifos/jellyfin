using System;
using System.Linq;

namespace Emby.Server.Implementations.Audiobooks;

/// <summary>
/// Latin American (Mexican) Spanish and Spanish from Spain are different narrations of the same book.
/// Latin American comes first; Spain is used when there is no Latin American edition.
/// Store titles often say it ("Narración en castellano", "Latino"); otherwise the store is a hint: the Spanish
/// catalog of the US store is Latin American, the Spanish store is from Spain.
/// </summary>
public static class SpanishVariant
{
    /// <summary>Latin American (Mexico and the rest of Latin America).</summary>
    public const string Latam = "latam";

    /// <summary>Spain (castellano).</summary>
    public const string Spain = "spain";

    private static readonly string[] _latamWords = ["latino", "latina", "latinoamerica", "latinoamericano", "latam", "mexico", "mexicano", "mexicana", "hispanoamerica", "neutro"];

    private static readonly string[] _spainWords = ["castellano", "espana", "castilian", "peninsular"];

    /// <summary>
    /// The variant named in a folder, file or title, if any.
    /// </summary>
    /// <param name="text">Name or path.</param>
    /// <returns>The variant, or null when not named.</returns>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var words = AudiobookCatalog.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => _latamWords.Contains(w, StringComparer.Ordinal)))
        {
            return Latam;
        }

        return words.Any(w => _spainWords.Contains(w, StringComparer.Ordinal)) ? Spain : null;
    }

    /// <summary>
    /// Extra score of an edition: Spanish over other languages, and the wanted Spanish over the other one.
    /// </summary>
    /// <param name="match">The edition.</param>
    /// <param name="wanted">The wanted variant.</param>
    /// <returns>The bonus.</returns>
    public static double Bonus(AudiobookMatch match, string wanted)
    {
        var named = Detect(match.Title + " " + match.Subtitle);
        var language = (match.Language ?? string.Empty).ToLowerInvariant();
        var spanish = named is not null || language.StartsWith("es", StringComparison.Ordinal) || language.StartsWith("spa", StringComparison.Ordinal);
        if (!spanish)
        {
            return 0;
        }

        // The edition says which Spanish it is
        if (named is not null)
        {
            return named == wanted ? 0.15 : 0.02;
        }

        // Otherwise the store hints it
        var hinted = match.Region switch
        {
            "us" => Latam,
            "es" => Spain,
            _ => null
        };

        return hinted is null ? 0.08 : hinted == wanted ? 0.10 : 0.06;
    }
}
