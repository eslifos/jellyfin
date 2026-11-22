using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Home;

namespace MediaBrowser.Controller.Home;

/// <summary>
/// The books of each audiobooks library (parts, lengths, sagas, authors, narrators), prepared in the background
/// and kept on disk: reading them for every visit meant one database query per book.
/// </summary>
public interface IAudiobookCatalog
{
    /// <summary>
    /// Gets the books of a library: the prepared list (refreshed in the background when old), or prepared now
    /// the very first time.
    /// </summary>
    /// <param name="libraryId">The library id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The books.</returns>
    Task<IReadOnlyList<AudiobookEntry>> GetAsync(Guid libraryId, CancellationToken cancellationToken);

    /// <summary>
    /// Prepares again the books of every books library (scheduled task).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task BuildAllAsync(CancellationToken cancellationToken);
}
