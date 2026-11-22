using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Emby.Server.Implementations.Audiobooks;

/// <summary>
/// Audible id (ASIN) of an audiobook, editable in "Edit metadata" and used to fix a wrong match.
/// </summary>
public class AudibleExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "Audible";

    /// <inheritdoc />
    public string Key => AudiobookSources.Audible;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Book;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is AudioBook or MusicAlbum;
}
