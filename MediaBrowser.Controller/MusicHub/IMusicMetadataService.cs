using System;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.MusicHub;

/// <summary>
/// Completes what albums and artists are missing with other music catalogs, for when the usual providers have nothing.
/// </summary>
public interface IMusicMetadataService
{
    /// <summary>
    /// Gets a value indicating whether a Last.fm key is saved. The key itself is never handed out.
    /// </summary>
    bool HasLastFmKey { get; }

    /// <summary>
    /// Fills in what is missing, little by little and without replacing anything that already exists.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task FillAsync(IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Checks a Last.fm key and saves it if it works.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the key is valid and was saved.</returns>
    Task<bool> SetLastFmKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets the Last.fm key.
    /// </summary>
    void RemoveLastFmKey();
}
