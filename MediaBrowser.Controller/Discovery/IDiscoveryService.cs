using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Discovery;

namespace MediaBrowser.Controller.Discovery;

/// <summary>
/// Home banner picks (recent and popular releases) and upcoming releases from TMDb.
/// </summary>
public interface IDiscoveryService
{
    /// <summary>
    /// Gets the most popular releases of this year or the last 6 months (movies, series and anime) visible to the user.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="limit">The maximum number of picks.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The picks, the most popular first.</returns>
    Task<IReadOnlyList<HeroPick>> GetHeroAsync(User user, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Gets upcoming episodes of the library's series and upcoming movies of its sagas between two dates.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="from">The first date.</param>
    /// <param name="to">The last date.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The releases by date.</returns>
    Task<IReadOnlyList<UpcomingRelease>> GetUpcomingAsync(User user, DateTime from, DateTime to, CancellationToken cancellationToken);
}
