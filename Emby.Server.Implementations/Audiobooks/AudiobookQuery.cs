using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Emby.Server.Implementations.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.Audiobooks;

/// <summary>
/// What is looked up for a book: its clean title, its author and year.
/// Libraries are organised as Author / Book / files, so the author comes from the tags or from the folder above the book.
/// </summary>
public sealed partial class AudiobookQuery
{
    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the author.</summary>
    public string? Author { get; set; }

    /// <summary>Gets or sets the year.</summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the Spanish wanted when the library is in Spanish: Latin American ("latam") or from Spain
    /// ("spain"). The folder or file name decides ("Latino", "Castellano"…), otherwise the country of the library.
    /// </summary>
    public string Variant { get; set; } = SpanishVariant.Latam;

    /// <summary>Gets or sets the language and country of the library.</summary>
    public MetadataLocale Locale { get; set; } = MetadataLocale.From(null, null);

    /// <summary>
    /// The same query with another title.
    /// </summary>
    /// <param name="title">The title to look up.</param>
    /// <returns>A copy with that title.</returns>
    public AudiobookQuery WithTitle(string title) => new() { Title = title, Author = Author, Year = Year, Variant = Variant, Locale = Locale };

    /// <summary>
    /// Builds the query of a book.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="name">Name of the item (folder or file name, or its title tag).</param>
    /// <param name="path">Path of the book: its folder, or its file.</param>
    /// <param name="isFolder">Whether the path is the book's folder (a book in parts).</param>
    /// <param name="artists">Authors from the tags.</param>
    /// <param name="year">Year, when known.</param>
    /// <param name="locale">The language and country of the library.</param>
    /// <returns>The query, or null when there is nothing to look up.</returns>
    public static AudiobookQuery? Create(ILibraryManager libraryManager, string? name, string? path, bool isFolder, IEnumerable<string>? artists, int? year, MetadataLocale locale)
    {
        var title = Clean(name);
        if (string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(path))
        {
            title = Clean(isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path));
        }

        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var author = artists?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))?.Trim();
        if (string.IsNullOrEmpty(author) && !string.IsNullOrEmpty(path))
        {
            author = AuthorFromFolders(libraryManager, path, isFolder, title);
        }

        // "Author - Title" in the name itself
        if (string.IsNullOrEmpty(author) && title.Split(" - ", 2, StringSplitOptions.TrimEntries) is [var first, var rest] && first.Length > 0 && rest.Length > 0)
        {
            author = first;
            title = rest;
        }

        var variant = SpanishVariant.Detect(name + " " + path);
        year ??= FindYear(name) ?? (path is null ? null : FindYear(Path.GetFileName(isFolder ? path : Path.GetDirectoryName(path))));
        var usualVariant = locale.Country == "es" ? SpanishVariant.Spain : SpanishVariant.Latam;
        return new AudiobookQuery { Title = title, Author = author, Year = year, Variant = variant ?? usualVariant, Locale = locale };
    }

    /// <summary>
    /// Whether an item is a part of a book stored as a folder of parts (those are not looked up on their own).
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="filePath">The file.</param>
    /// <returns>True for parts.</returns>
    public static bool IsPartOfBook(ILibraryManager libraryManager, string? filePath)
    {
        var dir = string.IsNullOrEmpty(filePath) ? null : Path.GetDirectoryName(filePath);
        return dir is not null && libraryManager.FindByPath(dir, true) is MusicAlbum;
    }

    /// <summary>
    /// The author folder: the folder above the book's folder, or the folder that holds a loose book file.
    /// </summary>
    private static string? AuthorFromFolders(ILibraryManager libraryManager, string path, bool isFolder, string title)
    {
        var dir = isFolder ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        // A file inside its own book folder: the author is one level up
        if (!isFolder && AudiobookCatalog.Normalize(Clean(Path.GetFileName(dir))) == AudiobookCatalog.Normalize(title))
        {
            dir = Path.GetDirectoryName(dir);
        }
        else if (isFolder)
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        // The library folder itself is not an author
        if (libraryManager.FindByPath(dir, true) is not Folder folder || folder.IsTopParent || folder is CollectionFolder)
        {
            return null;
        }

        var author = Clean(Path.GetFileName(dir));
        return string.IsNullOrEmpty(author) ? null : author;
    }

    /// <summary>Removes years, tags in brackets and words like "Audiolibro" or "Unabridged".</summary>
    internal static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var text = Brackets().Replace(name, " ");
        text = Noise().Replace(text, " ");
        text = text.Replace('_', ' ').Replace('.', ' ');
        text = Spaces().Replace(text, " ").Trim(' ', '-', '–');
        return text;
    }

    private static int? FindYear(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = YearInBrackets().Match(text);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex(@"[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\b(audiolibro|audiobook|audiolivro|hörbuch|livre audio|unabridged|abridged|completo|integro|íntegro|narrado por [^-]+|m4b|mp3)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\(\[](\d{4})[\)\]]")]
    private static partial Regex YearInBrackets();
}
