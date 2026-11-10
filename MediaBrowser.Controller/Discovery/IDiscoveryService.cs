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

    /// <summary>
    /// Gets the rows of the start screen of a movie library: top 10, trending, best rated, hidden gems, classics, upcoming and in theaters.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="parentId">The library id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rows.</returns>
    Task<LibraryDiscovery> GetLibraryAsync(User user, Guid parentId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the best trailer of a movie or series: library language first (es-MX), then the same language
    /// from other countries, English and the original language.
    /// </summary>
    /// <param name="tmdbId">The TMDb id.</param>
    /// <param name="isSeries">Whether it is a series.</param>
    /// <param name="language">The library language (e.g. es-419).</param>
    /// <param name="country">The library country (e.g. MX).</param>
    /// <param name="originalLanguage">The original language, if known.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The trailer, or <c>null</c>.</returns>
    Task<TrailerInfo?> GetTrailerAsync(int tmdbId, bool isSeries, string? language, string? country, string? originalLanguage, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the trailers to try, best first (same order as <see cref="GetTrailerAsync"/>), up to 6.
    /// </summary>
    /// <param name="tmdbId">The TMDb id.</param>
    /// <param name="isSeries">Whether it is a series.</param>
    /// <param name="language">The library language.</param>
    /// <param name="country">The library country.</param>
    /// <param name="originalLanguage">The original language, if known.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The candidates.</returns>
    Task<IReadOnlyList<TrailerInfo>> GetTrailerCandidatesAsync(int tmdbId, bool isSeries, string? language, string? country, string? originalLanguage, CancellationToken cancellationToken);
}
