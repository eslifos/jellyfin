using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Discovery;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Downloads each TMDb image once per size into the cache folder ("tmdb-images/{size}/{file}"). The cache cleaning
/// task removes the ones not downloaded again in 30 days; TMDb file names never change their picture, so a kept file
/// is always right.
/// </summary>
public sealed partial class TmdbImageCache : ITmdbImageCache
{
    private const string ImageBase = "https://image.tmdb.org/t/p/";
    private static readonly TimeSpan _retryAfter = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> _sizes = new(StringComparer.Ordinal) { "w92", "w154", "w185", "w300", "w342", "w500", "w780" };

    private readonly IApplicationPaths _paths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbImageCache> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _downloads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _failed = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbImageCache"/> class.
    /// </summary>
    /// <param name="paths">The application paths.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public TmdbImageCache(IApplicationPaths paths, IHttpClientFactory httpClientFactory, ILogger<TmdbImageCache> logger)
    {
        _paths = paths;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Whether a request names a TMDb size the apps may ask for and a plain TMDb file name (nothing else is fetched).
    /// </summary>
    /// <param name="size">The size.</param>
    /// <param name="file">The file name.</param>
    /// <returns>Whether it can be served.</returns>
    public static bool IsValid(string? size, string? file)
        => size is not null && file is not null && _sizes.Contains(size) && FileName().IsMatch(file);

    /// <inheritdoc />
    public Task<string?> GetImageAsync(string size, string file, CancellationToken cancellationToken)
    {
        if (!IsValid(size, file))
        {
            return Task.FromResult<string?>(null);
        }

        var path = Path.Combine(_paths.CachePath, "tmdb-images", size, file);
        if (File.Exists(path))
        {
            return Task.FromResult<string?>(path);
        }

        var key = size + "/" + file;
        if (_failed.TryGetValue(key, out var failedAt) && DateTime.UtcNow - failedAt < _retryAfter)
        {
            return Task.FromResult<string?>(null);
        }

        // Many cards ask for the same image at once: one download for all of them
        var download = _downloads.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => DownloadAsync(key, path)));
        return download.Value.WaitAsync(cancellationToken);
    }

    private async Task<string?> DownloadAsync(string key, string path)
    {
        // Written aside and then moved: a viewer never gets half a file
        var partial = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .GetAsync(new Uri(ImageBase + key), HttpCompletionOption.ResponseHeadersRead, CancellationToken.None)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) != true)
            {
                _logger.LogDebug("TMDb image {Key} not available: {Status}", key, response.StatusCode);
                _failed[key] = DateTime.UtcNow;
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await using (stream.ConfigureAwait(false))
            {
                await response.Content.CopyToAsync(stream).ConfigureAwait(false);
            }

            File.Move(partial, path, true);
            _failed.TryRemove(key, out _);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "TMDb image {Key} could not be downloaded", key);
            _failed[key] = DateTime.UtcNow;
            TryDelete(partial);
            return null;
        }
        finally
        {
            _downloads.TryRemove(key, out _);
        }
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Partial TMDb image {File} not removed", file);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\.(jpg|jpeg|png)$")]
    private static partial Regex FileName();
}
