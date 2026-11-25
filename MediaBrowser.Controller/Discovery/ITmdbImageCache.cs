using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Keeps TMDb images (posters and stills of upcoming releases) in the server cache, so the apps ask the server
/// for them at a small size and never depend on reaching TMDb themselves.
/// </summary>
public interface ITmdbImageCache
{
    /// <summary>
    /// Gets the local file of a TMDb image, downloading it first if needed.
    /// </summary>
    /// <param name="size">The TMDb size (w92, w154, w185, w300, w342, w500, w780).</param>
    /// <param name="file">The file name on TMDb ("abc123.jpg").</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The file path, or <c>null</c> when the request is not valid or TMDb does not have it.</returns>
    Task<string?> GetImageAsync(string size, string file, CancellationToken cancellationToken);
}
