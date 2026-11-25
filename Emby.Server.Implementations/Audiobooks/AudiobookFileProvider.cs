using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Audiobooks;

/// <summary>
/// Data of the books stored as one file (.m4b, .mp3…). The parts of a book stored as a folder are skipped:
/// the folder (album) gets the data.
/// </summary>
public class AudiobookFileProvider : IRemoteMetadataProvider<AudioBook, SongInfo>, IHasOrder
{
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AudiobookCatalog _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudiobookFileProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public AudiobookFileProvider(ILibraryManager libraryManager, IHttpClientFactory httpClientFactory, ILogger<AudiobookFileProvider> logger)
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
    public async Task<MetadataResult<AudioBook>> GetMetadata(SongInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<AudioBook> { Item = new AudioBook() };
        if (AudiobookQuery.IsPartOfBook(_libraryManager, info.Path))
        {
            return result;
        }

        var match = await AudiobookMetadata.FindAsync(_catalog, info.ProviderIds, Query(info), MetadataLocale.From(info.MetadataLanguage, info.MetadataCountryCode), cancellationToken).ConfigureAwait(false);
        if (match is not null)
        {
            AudiobookMetadata.Apply(result, match);
        }

        return result;
    }

    /// <inheritdoc />
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SongInfo searchInfo, CancellationToken cancellationToken)
        => AudiobookMetadata.SearchAsync(_catalog, Query(searchInfo), cancellationToken);

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken);

    private AudiobookQuery? Query(SongInfo info)
    {
        // The name of the book folder is better than a file name like "audiobook" or "01"
        var name = info.Name;
        if (!string.IsNullOrEmpty(info.Path) && IsGenericName(name ?? Path.GetFileNameWithoutExtension(info.Path))
            && Path.GetDirectoryName(info.Path) is { } dir && _libraryManager.FindByPath(dir, true) is Folder { IsTopParent: false })
        {
            name = Path.GetFileName(dir);
        }

        var artists = info.AlbumArtists is { Count: > 0 } ? info.AlbumArtists : info.Artists;
        return AudiobookQuery.Create(_libraryManager, name, info.Path, false, artists, info.Year, MetadataLocale.From(info.MetadataLanguage, info.MetadataCountryCode));
    }

    private static bool IsGenericName(string name)
        => System.Text.RegularExpressions.Regex.IsMatch(
            name.Trim(),
            @"^(audiobook|audiolibro|libro|book|full|completo|\d+|(part|parte|cd|disc|disco)\s*\d*)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
