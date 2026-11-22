using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Home;

namespace MediaBrowser.Controller.Home;

/// <summary>
/// The home page: the part that is the same for everybody is prepared by a scheduled task and kept on
/// disk, so opening the home page costs the server almost nothing.
/// </summary>
public interface IHomeSnapshotService
{
    /// <summary>
    /// Prepares the shared part of the home page (banner, library blocks, upcoming, genres) and saves it.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task BuildAsync(IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the prepared part of the home page, with only what the user can see.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The page.</returns>
    HomePageDto GetPage(User user);

    /// <summary>
    /// Gets the rows of the user: continue watching, next up, listening, favorites, music played, books.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The rows.</returns>
    HomePersonalDto GetPersonal(User user);

    /// <summary>
    /// Gets the prepared start page of one library (banner, what to watch today, rows, genres, upcoming).
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="libraryId">The library id.</param>
    /// <returns>The page.</returns>
    HomeLibraryPageDto GetLibraryPage(User user, Guid libraryId);

    /// <summary>
    /// Gets the rows of the user inside one library: continue watching, next up, listening, favorites.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="libraryId">The library id.</param>
    /// <returns>The rows.</returns>
    HomePersonalDto GetLibraryPersonal(User user, Guid libraryId);
}
