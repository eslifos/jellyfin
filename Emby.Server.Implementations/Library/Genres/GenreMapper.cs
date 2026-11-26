using System;
using System.Collections.Generic;
using System.Text;

namespace Emby.Server.Implementations.Library.Genres;

/// <summary>
/// Maps a list of genres with the equivalences of one language and drops the repeated ones.
/// Genres are compared by their key: lowercase, without spaces around, with "-" and "_" as spaces and "&amp;", "/"
/// and "+" as "and", so "Sci-Fi &amp; Fantasy" and "sci fi and fantasy" are the same genre.
/// </summary>
public static class GenreMapper
{
    /// <summary>
    /// Gets the key a genre is compared by.
    /// </summary>
    /// <param name="genre">The genre.</param>
    /// <returns>The key.</returns>
    public static string Key(string genre)
    {
        var text = new StringBuilder(genre.ToLowerInvariant())
            .Replace("&", " and ")
            .Replace("/", " and ")
            .Replace("+", " and ")
            .Replace('-', ' ')
            .Replace('_', ' ')
            .ToString();
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Builds the lookup of one language: every genre it maps from, and every genre it maps to (so the different
    /// spellings of a genre already in the language end up written the same way).
    /// </summary>
    /// <param name="entries">The equivalences, genre as it arrives → genre in the language.</param>
    /// <returns>The lookup, by key.</returns>
    public static IReadOnlyDictionary<string, string> BuildLookup(IEnumerable<KeyValuePair<string, string>> entries)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var entry in entries)
        {
            var from = entry.Key.Trim();
            var to = entry.Value.Trim();
            if (from.Length > 0 && to.Length > 0)
            {
                pairs.Add(new KeyValuePair<string, string>(from, to));
            }
        }

        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, to) in pairs)
        {
            lookup[Key(to)] = to;
        }

        // What a genre is mapped from wins over a spelling of the language
        foreach (var (from, to) in pairs)
        {
            lookup[Key(from)] = to;
        }

        return lookup;
    }

    /// <summary>
    /// Maps the genres and drops the repeated ones, keeping the order of the first time each one appears.
    /// </summary>
    /// <param name="genres">The genres of an item.</param>
    /// <param name="lookup">The lookup of the language (<see cref="BuildLookup"/>).</param>
    /// <returns>The genres in the language.</returns>
    public static string[] Map(IReadOnlyList<string> genres, IReadOnlyDictionary<string, string> lookup)
    {
        var result = new List<string>(genres.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var genre in genres)
        {
            if (string.IsNullOrWhiteSpace(genre))
            {
                continue;
            }

            var key = Key(genre);
            var mapped = lookup.TryGetValue(key, out var target) ? target : genre.Trim();
            if (seen.Add(Key(mapped)))
            {
                result.Add(mapped);
            }
        }

        return result.ToArray();
    }
}
