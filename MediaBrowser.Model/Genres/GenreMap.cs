using System.Collections.Generic;

namespace MediaBrowser.Model.Genres;

/// <summary>
/// One equivalence of the genre map: a genre as providers or NFO files bring it (usually in English) and the name
/// the library uses in its language.
/// </summary>
/// <param name="From">The genre as it arrives, like "Comedy".</param>
/// <param name="To">The genre in the language of the library, like "Comedia".</param>
/// <param name="IsDefault">Whether it is one of the built-in equivalences (not added or changed by an administrator).</param>
public sealed record GenreMapEntry(string From, string To, bool IsDefault);

/// <summary>
/// The genre map of one language.
/// </summary>
/// <param name="Language">The language code, like "es".</param>
/// <param name="Entries">The equivalences, sorted by the genre as it arrives.</param>
/// <param name="IsCustom">Whether an administrator changed the built-in equivalences of this language.</param>
/// <param name="InUse">Whether a library (or the server default) asks its metadata in this language.</param>
public sealed record GenreMapLanguage(string Language, IReadOnlyList<GenreMapEntry> Entries, bool IsCustom, bool InUse);

/// <summary>
/// The genre map of every language that has one.
/// </summary>
/// <param name="Languages">The languages.</param>
public sealed record GenreMap(IReadOnlyList<GenreMapLanguage> Languages);

/// <summary>
/// One equivalence sent by an administrator.
/// </summary>
/// <param name="From">The genre as it arrives.</param>
/// <param name="To">The genre in the language of the library.</param>
public sealed record GenreMapPair(string From, string To);

/// <summary>
/// The whole list of equivalences of one language, as an administrator saves it.
/// </summary>
/// <param name="Entries">The equivalences.</param>
public sealed record GenreMapUpdate(IReadOnlyList<GenreMapPair> Entries);
