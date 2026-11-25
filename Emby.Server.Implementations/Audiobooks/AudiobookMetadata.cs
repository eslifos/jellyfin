using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Emby.Server.Implementations.Audiobooks;

/// <summary>
/// Copies what the catalogs know about a book onto the item.
/// </summary>
public static partial class AudiobookMetadata
{
    /// <summary>Name shown in the library providers list.</summary>
    public const string ProviderName = "Audiolibros en línea";

    /// <summary>Catalogs whose id, when the item has it, is used directly (after "Identify").</summary>
    private static readonly string[] _byIdSources = [AudiobookSources.Audible, AudiobookSources.AppleBooks, AudiobookSources.GoogleBooks];

    /// <summary>
    /// Finds the book: by the id chosen in "Identify" or saved before, otherwise by title and author.
    /// </summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="providerIds">Ids the item already has.</param>
    /// <param name="query">Title and author.</param>
    /// <param name="locale">The language and country of the library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The book, or null.</returns>
    public static async Task<AudiobookMatch?> FindAsync(AudiobookCatalog catalog, IReadOnlyDictionary<string, string>? providerIds, AudiobookQuery? query, MetadataLocale locale, CancellationToken cancellationToken)
    {
        if (providerIds is not null)
        {
            foreach (var source in _byIdSources)
            {
                if (providerIds.TryGetValue(source, out var id) && !string.IsNullOrEmpty(id)
                    && await catalog.GetByIdAsync(source, id, locale, cancellationToken).ConfigureAwait(false) is { } byId)
                {
                    return byId;
                }
            }
        }

        return query is null ? null : await catalog.FindAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Results for "Identify": the best ones of every catalog.
    /// </summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="query">Title and author.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The results.</returns>
    public static async Task<IEnumerable<RemoteSearchResult>> SearchAsync(AudiobookCatalog catalog, AudiobookQuery? query, CancellationToken cancellationToken)
    {
        if (query is null)
        {
            return [];
        }

        var matches = await catalog.SearchAllAsync(query, 4, cancellationToken).ConfigureAwait(false);
        return matches.Select(m => new RemoteSearchResult
        {
            Name = CleanTitle(m.Title),
            Overview = m.Overview,
            ImageUrl = m.ImageUrl,
            ProductionYear = m.ReleaseDate?.Year,
            PremiereDate = m.ReleaseDate,
            SearchProviderName = SourceName(m.Source) + (m.Authors.Count > 0 ? " · " + string.Join(", ", m.Authors) : string.Empty),
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [m.Source] = m.Id }
        });
    }

    /// <summary>
    /// Fills the result with the book data.
    /// </summary>
    /// <typeparam name="T">Item type: a book in parts (album) or a book file.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="match">The book found.</param>
    public static void Apply<T>(MetadataResult<T> result, AudiobookMatch match)
        where T : BaseItem
    {
        var item = result.Item;
        item.Name = CleanTitle(match.Title);
        item.Overview = match.Overview;
        item.Genres = match.Genres.ToArray();
        item.Studios = string.IsNullOrEmpty(match.Publisher) ? [] : [match.Publisher];
        item.PremiereDate = match.ReleaseDate;
        item.ProductionYear = match.ReleaseDate?.Year;
        item.CommunityRating = match.Rating;
        item.IndexNumber = match.SeriesIndex;
        item.SetProviderId(match.Source, match.Id);
        if (!string.IsNullOrEmpty(match.Isbn))
        {
            item.SetProviderId("ISBN", match.Isbn);
        }

        var authors = match.Authors.ToArray();
        switch (item)
        {
            case AudioBook book:
                book.SeriesName = match.SeriesName;
                if (authors.Length > 0)
                {
                    book.Artists = authors;
                    book.AlbumArtists = authors;
                }

                break;
            case MusicAlbum album:
                if (authors.Length > 0)
                {
                    album.AlbumArtists = authors;
                    album.Artists = authors;
                }

                if (!string.IsNullOrEmpty(match.SeriesName))
                {
                    album.SetProviderId(AudiobookSources.SagaKey, match.SeriesName);
                }

                break;
        }

        foreach (var author in authors)
        {
            result.AddPerson(new PersonInfo { Name = author, Type = PersonKind.Author });
        }

        foreach (var narrator in match.Narrators)
        {
            result.AddPerson(new PersonInfo { Name = narrator, Type = PersonKind.Narrator });
        }

        result.HasMetadata = true;
        result.Provider = ProviderName;
    }

    /// <summary>Display name of a catalog.</summary>
    /// <param name="source">The catalog key.</param>
    /// <returns>Its name.</returns>
    public static string SourceName(string source) => source switch
    {
        AudiobookSources.AppleBooks => "Apple Books",
        AudiobookSources.GoogleBooks => "Google Books",
        AudiobookSources.OpenLibrary => "Open Library",
        _ => source
    };

    /// <summary>
    /// Titles from stores carry edition notes like "(Unabridged)" or "(Narración en Castellano)", the original
    /// title in square brackets ("[Nietzsche for Stressed]") and a long subtitle after a colon: the name keeps
    /// the title itself ("Nietzsche para estresados").
    /// </summary>
    /// <param name="title">The store title.</param>
    /// <returns>The clean title.</returns>
    internal static string CleanTitle(string title)
    {
        var text = EditionNoteAnywhere().Replace(title, " ");
        text = SquareBrackets().Replace(text, " ");
        text = Spaces().Replace(text, " ").Trim(' ', ':', '-', '–', ',');

        // "Title: a long subtitle" keeps the title; short ones ("Muerte en la montaña: el paso Dyatlov") stay whole
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon > 2 && text.Length > 48)
        {
            text = text[..colon].Trim();
        }

        return text.Length > 0 ? text : title.Trim();
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*(unabridged|abridged|narraci|narrado|íntegr|integr|audiolibro|castellano|latino|edición|edition)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex EditionNoteAnywhere();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex SquareBrackets();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
