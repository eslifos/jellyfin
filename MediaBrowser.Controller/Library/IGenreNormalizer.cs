using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Genres;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Keeps the genres of a library in its language: "Comedy" and "Comedia" are the same genre, so a Spanish library
/// keeps only "Comedia". Providers and NFO files that bring genres in English are mapped when an item is saved.
/// </summary>
public interface IGenreNormalizer
{
    /// <summary>
    /// Gets the genre map of every language that has one (built-in or added by an administrator).
    /// </summary>
    /// <param name="languagesInUse">The languages the libraries ask their metadata in.</param>
    /// <returns>The map.</returns>
    GenreMap GetMap(IReadOnlyCollection<string> languagesInUse);

    /// <summary>
    /// Replaces the equivalences of one language.
    /// </summary>
    /// <param name="language">The language code.</param>
    /// <param name="entries">The equivalences.</param>
    void SetMap(string language, IReadOnlyList<GenreMapPair> entries);

    /// <summary>
    /// Goes back to the built-in equivalences of one language.
    /// </summary>
    /// <param name="language">The language code.</param>
    void ResetMap(string language);

    /// <summary>
    /// Gets every genre name some language maps from (to find the items that still have them).
    /// </summary>
    /// <returns>The names.</returns>
    IReadOnlyCollection<string> GetSourceGenres();

    /// <summary>
    /// Maps the genres of an item to the language of its library, unless its genres are locked.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether its genres changed.</returns>
    bool Normalize(BaseItem item);
}
