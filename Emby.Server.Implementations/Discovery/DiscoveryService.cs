using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Imdb;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Discovery;
using MediaBrowser.Model.Entities;
using MediaBrowser.Providers.Plugins.Tmdb;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Home banner (recent and popular releases) and upcoming releases, using the library, the IMDb datasets and TMDb.
/// </summary>
public sealed class DiscoveryService : IDiscoveryService, IDisposable
{
    private static readonly TimeSpan _heroMaxAge = TimeSpan.FromHours(3);
    private static readonly TimeSpan _upcomingMaxAge = TimeSpan.FromHours(12);
    private static readonly TimeSpan _popularityMaxAge = TimeSpan.FromHours(24);
    private const int TmdbLookups = 40;

    private readonly ILibraryManager _libraryManager;
    private readonly TmdbClientManager _tmdb;
    private readonly IImdbRatingsService _imdb;
    private readonly IServerConfigurationManager _config;
    private readonly ILogger<DiscoveryService> _logger;
    private readonly SemaphoreSlim _heroLock = new(1, 1);
    private readonly SemaphoreSlim _upcomingLock = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTime At, double? Popularity)> _popularity = new(StringComparer.Ordinal);

    private (DateTime Built, List<HeroPick> Picks)? _hero;
    private (DateTime Built, List<UpcomingRelease> Releases)? _upcoming;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="tmdb">The TMDb client.</param>
    /// <param name="imdb">The IMDb ratings.</param>
    /// <param name="config">The server configuration.</param>
    /// <param name="logger">The logger.</param>
    public DiscoveryService(ILibraryManager libraryManager, TmdbClientManager tmdb, IImdbRatingsService imdb, IServerConfigurationManager config, ILogger<DiscoveryService> logger)
    {
        _libraryManager = libraryManager;
        _tmdb = tmdb;
        _imdb = imdb;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HeroPick>> GetHeroAsync(User user, int limit, CancellationToken cancellationToken)
    {
        var picks = _hero is { } cached && DateTime.UtcNow - cached.Built < _heroMaxAge ? cached.Picks : null;
        if (picks is null)
        {
            await _heroLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_hero is { } again && DateTime.UtcNow - again.Built < _heroMaxAge)
                {
                    picks = again.Picks;
                }
                else
                {
                    picks = await BuildHeroAsync(cancellationToken).ConfigureAwait(false);
                    _hero = (DateTime.UtcNow, picks);
                }
            }
            finally
            {
                _heroLock.Release();
            }
        }

        return picks
            .Where(p => _libraryManager.GetItemById(p.ItemId) is { } item && item.IsVisibleStandalone(user))
            .Take(Math.Clamp(limit, 1, 30))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UpcomingRelease>> GetUpcomingAsync(User user, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var releases = _upcoming is { } cached && DateTime.UtcNow - cached.Built < _upcomingMaxAge ? cached.Releases : null;
        if (releases is null)
        {
            await _upcomingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_upcoming is { } again && DateTime.UtcNow - again.Built < _upcomingMaxAge)
                {
                    releases = again.Releases;
                }
                else
                {
                    releases = await BuildUpcomingAsync(cancellationToken).ConfigureAwait(false);
                    _upcoming = (DateTime.UtcNow, releases);
                }
            }
            finally
            {
                _upcomingLock.Release();
            }
        }

        var visible = new Dictionary<Guid, bool>();
        bool IsVisible(Guid? id)
        {
            if (id is not Guid value)
            {
                return false;
            }

            if (!visible.TryGetValue(value, out var ok))
            {
                ok = _libraryManager.GetItemById(value) is { } item && item.IsVisibleStandalone(user);
                visible[value] = ok;
            }

            return ok;
        }

        return releases
            .Where(r => r.ReleaseDate >= from && r.ReleaseDate <= to)
            .Where(r => IsVisible(r.Kind == "Episode" ? r.SeriesId : r.RelatedItemId))
            .ToList();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _heroLock.Dispose();
        _upcomingLock.Dispose();
    }

    private static int? TmdbId(BaseItem item)
        => int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    private static DateTime ToUtcDate(DateTime date) => DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

    private async Task<List<HeroPick>> BuildHeroAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        // Releases of this year or of the last 6 months, whichever goes further back
        var start = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        if (now.AddMonths(-6) < start)
        {
            start = now.AddMonths(-6);
        }

        var end = now.AddDays(1);
        var windowDays = Math.Max(1, (now - start).TotalDays);
        var candidates = new Dictionary<Guid, (BaseItem Item, HeroPick Pick)>();

        void Add(BaseItem item, string reason, DateTime? date, int newEpisodes)
        {
            if (!item.HasImage(ImageType.Backdrop) || candidates.ContainsKey(item.Id))
            {
                return;
            }

            candidates[item.Id] = (item, new HeroPick { ItemId = item.Id, Reason = reason, ReleaseDate = date, NewEpisodes = newEpisodes });
        }

        // Movies released in the window (or of this year without an exact date)
        foreach (var movie in _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            IsVirtualItem = false,
            MinPremiereDate = start,
            MaxPremiereDate = end
        }))
        {
            Add(movie, "Movie", movie.PremiereDate, 0);
        }

        foreach (var movie in _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            IsVirtualItem = false,
            Years = [now.Year]
        }).Where(m => m.PremiereDate is null))
        {
            Add(movie, "Movie", null, 0);
        }

        // Series (and anime) with episodes aired in the window that are in the library
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
            MinPremiereDate = start,
            MaxPremiereDate = end
        }).OfType<Episode>();

        foreach (var group in episodes.Where(e => !e.SeriesId.Equals(Guid.Empty)).GroupBy(e => e.SeriesId))
        {
            if (_libraryManager.GetItemById(group.Key) is Series series)
            {
                var isNew = series.PremiereDate is DateTime first && first >= start;
                Add(series, isNew ? "NewSeries" : "NewEpisodes", group.Max(e => e.PremiereDate), group.Count());
            }
        }

        // Preliminary score with local data; TMDb popularity only for the best ones
        foreach (var (item, pick) in candidates.Values)
        {
            var imdb = _imdb.GetRating(item);
            pick.ImdbRating = imdb?.Rating;
            pick.ImdbVotes = imdb?.Votes;
            pick.Score = Score(item, pick, windowDays, now);
        }

        foreach (var (item, pick) in candidates.Values.OrderByDescending(c => c.Pick.Score).Take(TmdbLookups).ToList())
        {
            pick.TmdbPopularity = await GetPopularityAsync(item, cancellationToken).ConfigureAwait(false);
            pick.Score = Score(item, pick, windowDays, now);
        }

        var result = candidates.Values
            .Where(c => IsGoodEnough(c.Item, c.Pick))
            .OrderByDescending(c => c.Pick.Score)
            .Select(c => c.Pick)
            .Take(30)
            .ToList();

        _logger.LogInformation("Home banner: {Count} recent releases since {Start:yyyy-MM-dd}, {Kept} kept", candidates.Count, start, result.Count);
        return result;
    }

    /// <summary>
    /// Popularity = IMDb votes and TMDb popularity (how many people are watching it), plus the rating and how recent it is.
    /// </summary>
    private static double Score(BaseItem item, HeroPick pick, double windowDays, DateTime now)
    {
        var rating = pick.ImdbRating ?? item.CommunityRating ?? 0;
        var votes = pick.ImdbVotes ?? 0;
        var popularity = pick.TmdbPopularity ?? 0;
        var age = pick.ReleaseDate is DateTime date ? Math.Clamp((now - date).TotalDays / windowDays, 0, 1) : 0.6;

        return Math.Log10(1 + votes)
            + (1.5 * Math.Log10(1 + popularity))
            + (0.35 * rating)
            + (1.0 - age)
            + (pick.Reason == "NewSeries" ? 0.3 : 0);
    }

    /// <summary>Leaves out badly rated titles and titles nobody knows.</summary>
    private static bool IsGoodEnough(BaseItem item, HeroPick pick)
    {
        var rating = pick.ImdbRating ?? item.CommunityRating;
        if (rating is float r && r > 0 && r < 5.5)
        {
            return false;
        }

        var known = (pick.ImdbVotes ?? 0) >= 1000 || (pick.TmdbPopularity ?? 0) >= 10;
        return known || (item.CommunityRating ?? 0) >= 6.5;
    }

    private async Task<double?> GetPopularityAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (TmdbId(item) is not int id)
        {
            return null;
        }

        var key = (item is Series ? "tv" : "movie") + id.ToString(CultureInfo.InvariantCulture);
        if (_popularity.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < _popularityMaxAge)
        {
            return cached.Popularity;
        }

        double? popularity = null;
        try
        {
            popularity = item is Series
                ? (await _tmdb.GetSeriesAsync(id, "en-US", null, null, cancellationToken).ConfigureAwait(false))?.Popularity
                : (await _tmdb.GetMovieAsync(id, "en-US", null, null, cancellationToken).ConfigureAwait(false))?.Popularity;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "TMDb popularity of {Name} not available", item.Name);
        }

        _popularity[key] = (DateTime.UtcNow, popularity);
        return popularity;
    }

    private async Task<List<UpcomingRelease>> BuildUpcomingAsync(CancellationToken cancellationToken)
    {
        var today = DateTime.UtcNow.Date;
        var language = _config.Configuration.PreferredMetadataLanguage;
        var country = _config.Configuration.MetadataCountryCode;
        var result = new ConcurrentBag<UpcomingRelease>();

        // Next episodes of the series that are still running
        var series = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series], Recursive = true })
            .OfType<Series>()
            .Where(s => s.Status != SeriesStatus.Ended && TmdbId(s) is not null)
            .ToList();

        await Parallel.ForEachAsync(series, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (item, ct) =>
        {
            try
            {
                var id = TmdbId(item)!.Value;
                var show = await _tmdb.GetSeriesAsync(id, language, null, country, ct).ConfigureAwait(false);
                var next = show?.NextEpisodeToAir;
                if (next?.AirDate is null)
                {
                    return;
                }

                var season = await _tmdb.GetSeasonAsync(id, next.SeasonNumber, language, null, country, ct).ConfigureAwait(false);
                var upcoming = season?.Episodes?
                    .Where(e => e.AirDate is DateTime d && d.Date >= today)
                    .ToList();

                if (upcoming is null || upcoming.Count == 0)
                {
                    result.Add(Episode(item, id, next.Name, next.Overview, next.StillPath, next.AirDate.Value, next.SeasonNumber, (int)next.EpisodeNumber));
                    return;
                }

                foreach (var e in upcoming)
                {
                    result.Add(Episode(item, id, e.Name, e.Overview, e.StillPath, e.AirDate!.Value, e.SeasonNumber, (int)e.EpisodeNumber));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Upcoming episodes of {Name} not available", item.Name);
            }
        }).ConfigureAwait(false);

        // Future movies of the sagas (TMDb collections) the library has movies of
        var movies = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie], Recursive = true, IsVirtualItem = false })
            .OfType<Movie>()
            .ToList();
        var owned = movies.Select(TmdbId).Where(i => i is not null).Select(i => i!.Value).ToHashSet();
        var sagas = movies
            .Select(m => (Movie: m, Collection: m.GetProviderId(MetadataProvider.TmdbCollection)))
            .Where(x => int.TryParse(x.Collection, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .GroupBy(x => int.Parse(x.Collection!, CultureInfo.InvariantCulture))
            .ToList();

        await Parallel.ForEachAsync(sagas, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (saga, ct) =>
        {
            try
            {
                var collection = await _tmdb.GetCollectionAsync(saga.Key, language, null, country, ct).ConfigureAwait(false);
                foreach (var part in collection?.Parts ?? [])
                {
                    if (part.ReleaseDate is not DateTime date || date.Date < today.AddDays(-30) || owned.Contains(part.Id))
                    {
                        continue;
                    }

                    result.Add(new UpcomingRelease
                    {
                        Kind = "Movie",
                        TmdbId = part.Id,
                        Title = part.Title ?? string.Empty,
                        ReleaseDate = ToUtcDate(date),
                        Overview = part.Overview,
                        ImageUrl = _tmdb.GetPosterUrl(part.PosterPath),
                        CollectionName = collection!.Name,
                        RelatedItemId = saga.OrderByDescending(x => x.Movie.PremiereDate).First().Movie.Id
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "TMDb collection {Id} not available", saga.Key);
            }
        }).ConfigureAwait(false);

        var list = result.OrderBy(r => r.ReleaseDate).ThenBy(r => r.SeriesName ?? r.Title, StringComparer.Ordinal).ToList();
        _logger.LogInformation("Upcoming from TMDb: {Episodes} episodes of {Series} series, {Movies} saga movies", list.Count(r => r.Kind == "Episode"), series.Count, list.Count(r => r.Kind == "Movie"));
        return list;

        UpcomingRelease Episode(Series item, int tmdbId, string? name, string? overview, string? still, DateTime date, int season, int number) => new()
        {
            Kind = "Episode",
            TmdbId = tmdbId,
            Title = name ?? string.Empty,
            Overview = overview,
            ImageUrl = _tmdb.GetStillUrl(still),
            ReleaseDate = ToUtcDate(date),
            SeriesId = item.Id,
            SeriesName = item.Name,
            SeasonNumber = season,
            EpisodeNumber = number
        };
    }
}
