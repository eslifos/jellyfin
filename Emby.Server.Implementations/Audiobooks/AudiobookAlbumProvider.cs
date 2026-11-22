using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Audiobooks;

/// <summary>
/// Data of the books stored as a folder of parts (01.mp3, 02.mp3…), which are albums inside a books library.
/// Albums of music libraries are left to the music providers.
/// </summary>
public class AudiobookAlbumProvider : IRemoteMetadataProvider<MusicAlbum, AlbumInfo>, IHasOrder
{
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AudiobookCatalog _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudiobookAlbumProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public AudiobookAlbumProvider(ILibraryManager libraryManager, IHttpClientFactory httpClientFactory, ILogger<AudiobookAlbumProvider> logger)
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
    public async Task<MetadataResult<MusicAlbum>> GetMetadata(AlbumInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<MusicAlbum> { Item = new MusicAlbum() };
        if (!IsBook(info))
        {
            return result;
        }

        var match = await AudiobookMetadata.FindAsync(_catalog, info.ProviderIds, Query(info), cancellationToken).ConfigureAwait(false);
        if (match is not null)
        {
            AudiobookMetadata.Apply(result, match);
        }

        return result;
    }

    /// <inheritdoc />
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(AlbumInfo searchInfo, CancellationToken cancellationToken)
        => IsBook(searchInfo) ? AudiobookMetadata.SearchAsync(_catalog, Query(searchInfo), cancellationToken) : Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken);

    private AudiobookQuery? Query(AlbumInfo info)
        => AudiobookQuery.Create(_libraryManager, info.Name, info.Path, true, info.AlbumArtists, info.Year);

    private bool IsBook(AlbumInfo info)
        => !string.IsNullOrEmpty(info.Path)
            && _libraryManager.FindByPath(info.Path, true) is MusicAlbum album
            && _libraryManager.GetInheritedContentType(album) == CollectionType.books;
}
