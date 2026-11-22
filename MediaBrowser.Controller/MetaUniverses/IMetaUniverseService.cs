using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.MetaUniverses;

namespace MediaBrowser.Controller.MetaUniverses;

/// <summary>
/// Meta universes: franchises that cross libraries (movies, series, soundtracks, collections).
/// </summary>
public interface IMetaUniverseService
{
    /// <summary>
    /// Gets the universes with content visible to the user.
    /// </summary>
    /// <param name="user">The user (null = everything).</param>
    /// <param name="includeHidden">Whether to include hidden and empty universes (administrators).</param>
    /// <returns>The universes, the biggest first.</returns>
    IReadOnlyList<MetaUniverseSummary> GetUniverses(User? user, bool includeHidden);

    /// <summary>
    /// Gets one universe.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="user">The user (null = everything).</param>
    /// <returns>The universe, or null when it does not exist.</returns>
    MetaUniverseDetails? GetUniverse(string id, User? user);

    /// <summary>
    /// Creates a custom universe (empty id) or saves changes to an existing one.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The stored definition.</returns>
    MetaUniverseDefinition Save(MetaUniverseDefinition definition);

    /// <summary>
    /// Deletes a custom universe, or resets a built-in / automatic one to its defaults.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>Whether something was removed.</returns>
    bool Delete(string id);

    /// <summary>
    /// Prepares the universes and what each user can see of them, while the previous ones keep being served.
    /// </summary>
    /// <param name="force">Build even when the libraries have not changed.</param>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that finishes when they are ready.</returns>
    Task BuildAsync(bool force, IProgress<double> progress, CancellationToken cancellationToken);
}
