using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Audiobooks;

/// <summary>
/// Covers of audiobooks: the book already identified first, then the other catalogs (largest first) to choose from.
/// </summary>
public class AudiobookImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AudiobookCatalog _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudiobookImageProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public AudiobookImageProvider(ILibraryManager libraryManager, IHttpClientFactory httpClientFactory, ILogger<AudiobookImageProvider> logger)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _catalog = new AudiobookCatalog(httpClientFactory, logger);
    }

    /// <inheritdoc />
    public string Name => AudiobookMetadata.ProviderName;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item switch
    {
        AudioBook book => !AudiobookQuery.IsPartOfBook(_libraryManager, book.Path),
        MusicAlbum album => _libraryManager.GetInheritedContentType(album) == CollectionType.books,
        _ => false
    };

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => [ImageType.Primary];

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var locale = MetadataLocale.From(item.GetPreferredMetadataLanguage(), item.GetPreferredMetadataCountryCode());
        var query = item switch
        {
            MusicAlbum album => AudiobookQuery.Create(_libraryManager, album.Name, album.Path, true, album.AlbumArtists, album.ProductionYear, locale),
            AudioBook book => AudiobookQuery.Create(_libraryManager, book.Name, book.Path, false, book.AlbumArtists.Count > 0 ? book.AlbumArtists : book.Artists, book.ProductionYear, locale),
            _ => null
        };

        var images = new List<RemoteImageInfo>();
        var known = await AudiobookMetadata.FindAsync(_catalog, item.ProviderIds, null, locale, cancellationToken).ConfigureAwait(false);
        if (known is not null)
        {
            Add(images, known, 10);
        }

        if (query is not null)
        {
            // Only books that match: a book no catalog has gets the cover inside its files
            // (AudiobookEmbeddedCoverProvider), not the cover of a book with a similar title
            foreach (var match in await _catalog.SearchAllAsync(query, 3, cancellationToken).ConfigureAwait(false))
            {
                if (match.Score < AudiobookCatalog.MinScore)
                {
                    continue;
                }

                Add(images, match, match.Score * 5);
            }
        }

        return images
            .GroupBy(i => i.Url)
            .Select(g => g.First())
            .OrderByDescending(i => i.CommunityRating)
            .ThenByDescending(i => i.Width ?? 0)
            .ToList();
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken);

    private void Add(List<RemoteImageInfo> images, AudiobookMatch match, double rank)
    {
        if (string.IsNullOrEmpty(match.ImageUrl))
        {
            return;
        }

        images.Add(new RemoteImageInfo
        {
            ProviderName = Name + " · " + AudiobookMetadata.SourceName(match.Source),
            Url = match.ImageUrl,
            Type = ImageType.Primary,
            Width = match.ImageSize,
            Height = match.ImageSize,
            // Used only to sort: the identified book first, then the closest matches
            CommunityRating = rank
        });
    }
}
