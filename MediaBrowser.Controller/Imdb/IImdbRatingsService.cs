using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.Controller.Imdb;

/// <summary>
/// IMDb ratings and votes for the library, from the official IMDb datasets (refreshed daily by a scheduled task).
/// </summary>
public interface IImdbRatingsService
{
    /// <summary>
    /// Gets the date of the data in use, or null when it has not been downloaded yet.
    /// </summary>
    DateTime? LastUpdated { get; }

    /// <summary>
    /// Gets the IMDb rating of a movie, series or episode (episodes without their own IMDb id are found through their series).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The rating, or null when unknown.</returns>
    ImdbRating? GetRating(BaseItem item);

    /// <summary>
    /// Downloads the datasets, rebuilds the local database and fills missing IMDb ids of episodes in the library.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task UpdateAsync(IProgress<double> progress, CancellationToken cancellationToken);
}
