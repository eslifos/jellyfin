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
using TMDbLib.Objects.Find;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Home banner (recent and popular releases) and upcoming releases, using the library, the IMDb datasets and TMDb.
/// </summary>
public sealed class DiscoveryService : IDiscoveryService, IDisposable
{
    private static readonly TimeSpan _heroMaxAge = TimeSpan.FromHours(3);
    private static readonly TimeSpan _upcomingMaxAge = TimeSpan.FromHours(12);
    private static readonly TimeSpan _upcomingDirtyDelay = TimeSpan.FromMinutes(10);
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
    private readonly ConcurrentDictionary<Guid, int?> _seriesTmdb = new();
    private volatile bool _upcomingDirty;

    private (DateTime Built, List<HeroPick> Picks)? _hero;
    private (DateTime Built, List<UpcomingRelease> Releases)? _upcoming;
    private readonly ConcurrentDictionary<string, (DateTime Built, LibraryDiscovery Rows)> _libraryRows = new(StringComparer.Ordinal);
    private static readonly TimeSpan _libraryMaxAge = TimeSpan.FromMinutes(30);

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
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemUpdated += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
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
        var releases = _upcoming is { } cached && IsUpcomingFresh(cached.Built) ? cached.Releases : null;
        if (releases is null)
        {
            await _upcomingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_upcoming is { } again && IsUpcomingFresh(again.Built))
                {
                    releases = again.Releases;
                }
                else
                {
                    _upcomingDirty = false;
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
    public async Task<LibraryDiscovery> GetLibraryAsync(User user, Guid parentId, CancellationToken cancellationToken)
    {
        var key = user.Id.ToString("N", CultureInfo.InvariantCulture) + parentId.ToString("N", CultureInfo.InvariantCulture);
        if (_libraryRows.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Built < _libraryMaxAge)
        {
            return cached.Rows;
        }

        if (_libraryManager.GetItemById(parentId) is not Folder library)
        {
            return new LibraryDiscovery();
        }

        var language = library.GetPreferredMetadataLanguage();
        var country = library.GetPreferredMetadataCountryCode();
        if (string.IsNullOrEmpty(country))
        {
            country = "US";
        }

        var movies = library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            IsVirtualItem = false
        })
            // Only the main version of movies with several copies: the app does not list the other versions
            .Where(m => m is not Video { PrimaryVersionId: not null })
            .ToList();

        var byTmdb = new Dictionary<int, BaseItem>();
        foreach (var movie in movies)
        {
            if (TmdbId(movie) is int id)
            {
                byTmdb.TryAdd(id, movie);
            }
        }

        async Task<IReadOnlyList<TMDbLib.Objects.Search.SearchMovie>> List(string name, int pages)
        {
            try
            {
                return await _tmdb.GetMovieListAsync(name, language, country, pages, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "TMDb list {List} not available", name);
                return [];
            }
        }

        var day = await List("trending-day", 5).ConfigureAwait(false);
        var week = await List("trending-week", 5).ConfigureAwait(false);
        var popular = await List("popular", 5).ConfigureAwait(false);
        var upcoming = await List("upcoming", 3).ConfigureAwait(false);
        var nowPlaying = await List("now-playing", 3).ConfigureAwait(false);

        // Top 10: what people watch right now (trending this week and today, and popular), best ranked first
        var heat = new Dictionary<Guid, double>();
        void Heat(IReadOnlyList<TMDbLib.Objects.Search.SearchMovie> list, double weight)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (byTmdb.TryGetValue(list[i].Id, out var item))
                {
                    heat[item.Id] = heat.GetValueOrDefault(item.Id) + (weight * (list.Count - i) / list.Count);
                }
            }
        }

        Heat(week, 1.0);
        Heat(day, 0.8);
        Heat(popular, 0.5);

        var ratings = movies.ToDictionary(m => m.Id, m => _imdb.GetRating(m));
        double Weighted(BaseItem m, double minVotes)
        {
            var r = ratings.GetValueOrDefault(m.Id);
            if (r is null || r.Votes <= 0)
            {
                return (m.CommunityRating ?? 0) * 0.5;
            }

            return (r.Votes / (r.Votes + minVotes) * r.Rating) + (minVotes / (r.Votes + minVotes) * 6.8);
        }

        var top10 = heat.OrderByDescending(h => h.Value).Select(h => h.Key).Take(10).ToList();
        if (top10.Count < 10)
        {
            // Not enough trending titles: the most voted recent movies of the library
            var recent = DateTime.UtcNow.AddYears(-3);
            top10.AddRange(movies
                .Where(m => !top10.Contains(m.Id) && (m.PremiereDate ?? DateTime.MinValue) >= recent)
                .OrderByDescending(m => Math.Log10(1 + (ratings.GetValueOrDefault(m.Id)?.Votes ?? 0)) * Weighted(m, 5000))
                .Select(m => m.Id)
                .Take(10 - top10.Count));
        }

        var trending = day.Concat(week)
            .Select(t => byTmdb.GetValueOrDefault(t.Id))
            .Where(m => m is not null)
            .Select(m => m!.Id)
            .Distinct()
            .Take(30)
            .ToList();

        var topRated = movies
            .Where(m => (ratings.GetValueOrDefault(m.Id)?.Votes ?? 0) >= 25000)
            .OrderByDescending(m => Weighted(m, 25000))
            .Select(m => m.Id)
            .Take(30)
            .ToList();

        var seed = DateTime.UtcNow.DayOfYear;
        var gems = movies
            .Where(m => ratings.GetValueOrDefault(m.Id) is { Rating: >= 7.0f, Votes: >= 1500 and <= 60000 } && !m.IsPlayed(user, null))
            .OrderByDescending(m => Weighted(m, 1500))
            .Take(60)
            .OrderBy(m => (m.Id.GetHashCode() ^ seed) & 0xffff)
            .Select(m => m.Id)
            .Take(30)
            .ToList();

        var classics = movies
            .Where(m => m.ProductionYear is > 0 and <= 1990 && (ratings.GetValueOrDefault(m.Id)?.Votes ?? 0) >= 15000)
            .OrderByDescending(m => Weighted(m, 15000))
            .Select(m => m.Id)
            .Take(30)
            .ToList();

        var today = DateTime.UtcNow.Date;
        UpcomingRelease ToRelease(TMDbLib.Objects.Search.SearchMovie m, string kind) => new()
        {
            Kind = kind,
            TmdbId = m.Id,
            Title = m.Title ?? m.OriginalTitle ?? string.Empty,
            ReleaseDate = ToUtcDate(m.ReleaseDate ?? today),
            Overview = m.Overview,
            ImageUrl = _tmdb.GetImageUrl("w342", m.PosterPath),
            BackdropUrl = _tmdb.GetImageUrl("w1280", m.BackdropPath),
            Rating = m.VoteAverage > 0 ? m.VoteAverage : null,
            RelatedItemId = byTmdb.GetValueOrDefault(m.Id)?.Id
        };

        var rows = new LibraryDiscovery
        {
            Top10 = top10,
            Trending = trending,
            TopRated = topRated,
            HiddenGems = gems,
            Classics = classics,
            Upcoming = upcoming
                .Where(m => m.ReleaseDate is DateTime d && d.Date > today && !byTmdb.ContainsKey(m.Id) && !string.IsNullOrEmpty(m.PosterPath))
                .OrderBy(m => m.ReleaseDate)
                .Take(20)
                .Select(m => ToRelease(m, "Upcoming"))
                .ToList(),
            NowPlaying = nowPlaying
                .Where(m => !string.IsNullOrEmpty(m.PosterPath))
                .OrderByDescending(m => m.Popularity)
                .Take(20)
                .Select(m => ToRelease(m, "NowPlaying"))
                .ToList()
        };

        _libraryRows[key] = (DateTime.UtcNow, rows);
        return rows;
    }

    /// <inheritdoc />
    public async Task<TrailerInfo?> GetTrailerAsync(int tmdbId, bool isSeries, string? language, string? country, string? originalLanguage, CancellationToken cancellationToken)
        => (await GetTrailerCandidatesAsync(tmdbId, isSeries, language, country, originalLanguage, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrailerInfo>> GetTrailerCandidatesAsync(int tmdbId, bool isSeries, string? language, string? country, string? originalLanguage, CancellationToken cancellationToken)
    {
        var candidates = new List<TrailerInfo>();
        // Languages to try, best first
        var lang = string.IsNullOrEmpty(language) ? "en" : language;
        var baseLang = lang.Split('-')[0].ToLowerInvariant();
        var region = !string.IsNullOrEmpty(country) ? country.ToUpperInvariant() : (lang.Contains('-', StringComparison.Ordinal) ? lang.Split('-')[1].ToUpperInvariant() : null);
        if (region == "419")
        {
            region = "MX";
        }

        var tries = new List<string>();
        void Try(string code)
        {
            if (!tries.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                tries.Add(code);
            }
        }

        if (region is not null)
        {
            Try(baseLang + "-" + region);
        }

        if (baseLang == "es")
        {
            Try("es-MX");
            Try("es-AR");
            Try("es-ES");
        }

        Try(baseLang);
        Try("en-US");
        Try("en");

        var original = originalLanguage;
        if (string.IsNullOrEmpty(original) && !isSeries)
        {
            try
            {
                original = (await _tmdb.GetMovieAsync(tmdbId, null, null, null, cancellationToken).ConfigureAwait(false))?.OriginalLanguage;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "TMDb movie {Id} not available", tmdbId);
            }
        }

        if (!string.IsNullOrEmpty(original))
        {
            Try(original.Split('-')[0].ToLowerInvariant());
        }

        foreach (var code in tries)
        {
            IReadOnlyList<TMDbLib.Objects.General.Video> videos;
            try
            {
                videos = await _tmdb.GetVideosAsync(tmdbId, isSeries, code, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "TMDb videos of {Id} in {Language} not available", tmdbId, code);
                continue;
            }

            // Trailer over teaser, official first, dubbed over subtitled, newest first; two per language
            var ranked = videos
                .Where(v => string.Equals(v.Site, "YouTube", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v.Key)
                    && (string.Equals(v.Type, "Trailer", StringComparison.OrdinalIgnoreCase) || string.Equals(v.Type, "Teaser", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(v => string.Equals(v.Type, "Trailer", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(v => v.Official)
                .ThenByDescending(v => (v.Name ?? string.Empty).Contains("doblad", StringComparison.OrdinalIgnoreCase))
                .ThenBy(v => (v.Name ?? string.Empty).Contains("subtitulad", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(v => v.PublishedAt)
                .Take(2);

            foreach (var video in ranked)
            {
                if (!candidates.Any(c => c.Key == video.Key))
                {
                    candidates.Add(new TrailerInfo { Key = video.Key!, Name = video.Name ?? string.Empty, Language = code });
                }
            }

            if (candidates.Count >= 6)
            {
                break;
            }
        }

        return candidates;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemUpdated -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
        _heroLock.Dispose();
        _upcomingLock.Dispose();
    }

    private static int? TmdbId(BaseItem item)
        => int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    /// <summary>Rebuilt every 12 hours, or 10 minutes after series or movies are added, identified or removed (new libraries too).</summary>
    private bool IsUpcomingFresh(DateTime built)
    {
        var age = DateTime.UtcNow - built;
        return age < _upcomingMaxAge && !(_upcomingDirty && age > _upcomingDirtyDelay);
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Series or Movie)
        {
            _upcomingDirty = true;
            if (e.Item is Series)
            {
                _seriesTmdb.TryRemove(e.Item.Id, out _);
            }
        }
    }

    /// <summary>
    /// TMDb id of a series. Libraries identified with other providers (TheTVDB, AniDB, AniList…) have no TMDb id,
    /// so it is looked up from the TheTVDB or IMDb id, and as a last resort by exact name and year.
    /// </summary>
    private async Task<int?> ResolveSeriesTmdbAsync(Series series, string language, string? country, CancellationToken cancellationToken)
    {
        if (TmdbId(series) is int id)
        {
            return id;
        }

        if (_seriesTmdb.TryGetValue(series.Id, out var cached))
        {
            return cached;
        }

        int? found = null;
        try
        {
            foreach (var (provider, source) in new[] { (MetadataProvider.Tvdb, FindExternalSource.TvDb), (MetadataProvider.Imdb, FindExternalSource.Imdb) })
            {
                var external = series.GetProviderId(provider);
                if (string.IsNullOrEmpty(external))
                {
                    continue;
                }

                var result = await _tmdb.FindByExternalIdAsync(external, source, language, country, cancellationToken).ConfigureAwait(false);
                if (result?.TvResults is { Count: > 0 } tv)
                {
                    found = tv[0].Id;
                    break;
                }
            }

            if (found is null && series.ProductionYear is int year && year > 0 && !string.IsNullOrEmpty(series.Name))
            {
                var names = new[] { series.Name, series.OriginalTitle }.Where(n => !string.IsNullOrEmpty(n)).ToList();
                var hits = await _tmdb.SearchSeriesAsync(series.OriginalTitle ?? series.Name, language, country, year, cancellationToken).ConfigureAwait(false);
                found = hits?.FirstOrDefault(h => names.Any(n =>
                    string.Equals(h.Name, n, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(h.OriginalName, n, StringComparison.OrdinalIgnoreCase)))?.Id;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "TMDb id of {Name} not found", series.Name);
            return null; // try again on the next build
        }

        _seriesTmdb[series.Id] = found;
        return found;
    }

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
        // Every library (shows, movies, mixed, anime...), whatever provider identified the series
        var series = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series], Recursive = true, IsVirtualItem = false })
            .OfType<Series>()
            .Where(s => s.Status != SeriesStatus.Ended)
            .ToList();
        var lookupLanguage = string.IsNullOrEmpty(language) ? "en" : language;
        var withoutTmdb = 0;

        await Parallel.ForEachAsync(series, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (item, ct) =>
        {
            try
            {
                if (await ResolveSeriesTmdbAsync(item, lookupLanguage, country, ct).ConfigureAwait(false) is not int id)
                {
                    Interlocked.Increment(ref withoutTmdb);
                    return;
                }

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
        _logger.LogInformation("Upcoming from TMDb: {Episodes} episodes of {Series} running series ({Missing} not found on TMDb), {Movies} saga movies", list.Count(r => r.Kind == "Episode"), series.Count, withoutTmdb, list.Count(r => r.Kind == "Movie"));
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
