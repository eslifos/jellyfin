using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Downloads trailers (YouTube keys from TMDb) to the cache as plain MP4 files, so the app plays them with its own player.
/// </summary>
public interface ITrailerVideoService
{
    /// <summary>
    /// Gets the local MP4 file of a trailer, downloading it first if needed.
    /// </summary>
    /// <param name="key">The YouTube video key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The file path, or <c>null</c> if it could not be downloaded.</returns>
    Task<string?> GetVideoAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the local file of a trailer only if it was already downloaded.
    /// </summary>
    /// <param name="key">The YouTube video key.</param>
    /// <returns>The file path, or <c>null</c>.</returns>
    string? GetCachedVideo(string key);
}
