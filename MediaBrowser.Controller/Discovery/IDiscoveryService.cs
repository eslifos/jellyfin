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
    /// <param name="theme">"anime" or "kids": in theaters and upcoming only show movies of that kind.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rows.</returns>
    Task<LibraryDiscovery> GetLibraryAsync(User user, Guid parentId, string? theme, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the top 10 of movies or series across all the user's libraries: what people watch right now on TMDb
    /// (trending this week and today, popular), completed with the most voted recent titles.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="series">True for series, false for movies.</param>
    /// <param name="automatic">True to get the automatic list even when the top 10 is picked by hand.</param>
    /// <param name="parentId">Only the titles of this library (all the libraries when null).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The item ids, best ranked first.</returns>
    Task<IReadOnlyList<Guid>> GetTop10Async(User user, bool series, bool automatic, Guid? parentId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets how the top 10 rows are built (automatic or by hand).
    /// </summary>
    /// <returns>The settings.</returns>
    Top10Settings GetTop10Settings();

    /// <summary>
    /// Gets the banner picks of every library, without filtering by user (for the prepared home page).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The picks, the best first.</returns>
    Task<IReadOnlyList<HeroPick>> GetHeroPicksAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the top 10 of a library, or of every library together (by hand or automatic), without filtering by user.
    /// </summary>
    /// <param name="series">True for series, false for movies.</param>
    /// <param name="libraryId">The library, or null for all of them.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The item ids, best ranked first.</returns>
    Task<IReadOnlyList<Guid>> GetTop10IdsAsync(bool series, Guid? libraryId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the upcoming releases of every library, without filtering by user (for the prepared home page).
    /// </summary>
    /// <param name="from">First date.</param>
    /// <param name="to">Last date.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The releases by date.</returns>
    Task<IReadOnlyList<UpcomingRelease>> GetUpcomingAllAsync(DateTime from, DateTime to, CancellationToken cancellationToken);

    /// <summary>
    /// Saves how the top 10 rows are built and rebuilds them.
    /// </summary>
    /// <param name="settings">The settings.</param>
    void SaveTop10Settings(Top10Settings settings);

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
