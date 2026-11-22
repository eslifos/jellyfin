using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Home;
using MediaBrowser.Controller.Imdb;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Discovery;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Home;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Home;

/// <summary>
/// Prepares the home page. Everything that is the same for everybody (banner, one block of rows per library,
/// upcoming releases, genres) is built by a scheduled task every few hours and kept on disk; a visit only
/// adds the rows of the user, asked apart. The rows of what is new (added, released, on air) are prepared again
/// a minute after the libraries change, so something just added shows up without waiting for the task.
/// </summary>
public sealed class HomeSnapshotService : IHomeSnapshotService, IDisposable
{
    private const int RowSize = 20;
    private const int HeroSize = 10;
    private const int SpotlightSize = 12;

    /// <summary>Raised when the saved file changes shape: an older file is still shown while a new one is prepared.</summary>
    private const int SnapshotVersion = 4;
    private static readonly TimeSpan _genresMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan _visibilityMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>Quiet time after the last library change before the rows of what is new are prepared again.</summary>
    private static readonly TimeSpan _recentDelay = TimeSpan.FromMinutes(1);

    /// <summary>The rows that change when something is added, released or airs.</summary>
    private static readonly HashSet<string> _recentKeys = new(StringComparer.Ordinal)
    {
        "latestmovies", "releases", "newepisodes", "newseries", "onair", "newalbums", "latestbooks", "latest", "movies"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IDiscoveryService _discovery;
    private readonly IImdbRatingsService _imdb;
    private readonly IImageProcessor _imageProcessor;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserViewManager _userViewManager;
    private readonly ITVSeriesManager _tvSeriesManager;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<HomeSnapshotService> _logger;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private readonly Timer _recentTimer;
    private readonly ConcurrentDictionary<Guid, (DateTime At, DateTime Built, ConcurrentDictionary<Guid, bool> Visible)> _visibility = new();

    private HomeSnapshot? _snapshot;
    private int _loaded;
    private int _firstBuildStarted;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeSnapshotService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="discovery">The discovery service (banner, top 10, upcoming).</param>
    /// <param name="imdb">The IMDb ratings.</param>
    /// <param name="imageProcessor">The image processor.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="userViewManager">The user view manager.</param>
    /// <param name="tvSeriesManager">The TV series manager.</param>
    /// <param name="paths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public HomeSnapshotService(
        ILibraryManager libraryManager,
        IDiscoveryService discovery,
        IImdbRatingsService imdb,
        IImageProcessor imageProcessor,
        IUserDataManager userDataManager,
        IUserViewManager userViewManager,
        ITVSeriesManager tvSeriesManager,
        IApplicationPaths paths,
        ILogger<HomeSnapshotService> logger)
    {
        _libraryManager = libraryManager;
        _discovery = discovery;
        _imdb = imdb;
        _imageProcessor = imageProcessor;
        _userDataManager = userDataManager;
        _userViewManager = userViewManager;
        _tvSeriesManager = tvSeriesManager;
        _paths = paths;
        _logger = logger;

        _recentTimer = new Timer(_ => _ = RefreshRecentRowsAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemUpdated += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
    }

    private string SnapshotPath => Path.Combine(_paths.DataPath, "home", "snapshot.json");

    /// <inheritdoc />
    public async Task BuildAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _buildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = Load();
            var snapshot = new HomeSnapshot { Version = SnapshotVersion, Built = DateTime.UtcNow };

            // Banner: recent and popular releases (with a backdrop)
            var picks = await _discovery.GetHeroPicksAsync(cancellationToken).ConfigureAwait(false);
            foreach (var pick in picks)
            {
                if (snapshot.Hero.Count >= HeroSize)
                {
                    break;
                }

                if (_libraryManager.GetItemById(pick.ItemId) is { } item && item.HasImage(ImageType.Backdrop, 0))
                {
                    var card = Card(item, withDetails: true);
                    card.Note = pick.Reason;
                    card.NoteCount = pick.NewEpisodes > 0 ? pick.NewEpisodes : null;
                    snapshot.Hero.Add(card);
                }
            }

            progress.Report(10);
            await Breathe(cancellationToken).ConfigureAwait(false);

            // One block per library, with the rows that suit its kind
            var libraries = Libraries().ToList();
            for (var i = 0; i < libraries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var block = await BuildBlockAsync(libraries[i], cancellationToken).ConfigureAwait(false);
                if (block.Rows.Count > 0)
                {
                    snapshot.Blocks.Add(block);
                }

                progress.Report(10 + (70.0 * (i + 1) / Math.Max(1, libraries.Count)));
                await Breathe(cancellationToken).ConfigureAwait(false);
            }

            // Few recent releases: the banner is completed with well rated titles of the libraries
            var fill = Interleave(snapshot.Blocks.Where(IsWatchBlock).Select(b => b.Spotlight)).ToList();
            foreach (var card in fill)
            {
                if (snapshot.Hero.Count >= HeroSize)
                {
                    break;
                }

                if (!snapshot.Hero.Any(h => h.Id.Equals(card.Id)))
                {
                    snapshot.Hero.Add(card);
                }
            }

            // Next episodes and saga movies of the next three weeks (kept by the discovery service)
            try
            {
                var now = DateTime.UtcNow.Date;
                var upcoming = (await _discovery.GetUpcomingAllAsync(now, now.AddDays(21), cancellationToken).ConfigureAwait(false))
                    .Where(r => r.SeriesId is not null || r.RelatedItemId is not null)
                    .ToList();
                snapshot.Upcoming = upcoming.Take(40).ToList();

                // And the ones of each library, for its own page
                foreach (var release in upcoming)
                {
                    if ((release.Kind == "Episode" ? release.SeriesId : release.RelatedItemId) is not Guid id
                        || _libraryManager.GetItemById(id) is not { } item)
                    {
                        continue;
                    }

                    foreach (var folder in _libraryManager.GetCollectionFolders(item))
                    {
                        if (snapshot.Blocks.Find(b => b.LibraryId.Equals(folder.Id)) is { Upcoming.Count: < 30 } block)
                        {
                            block.Upcoming.Add(release);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Upcoming releases of the home page not available");
                snapshot.Upcoming = previous?.Upcoming ?? [];
            }

            progress.Report(85);

            // Counting every title of every genre is heavy: once a day
            if (previous is not null && DateTime.UtcNow - previous.GenresBuilt < _genresMaxAge && previous.Genres.Count > 0)
            {
                snapshot.Genres = previous.Genres;
                snapshot.GenresBuilt = previous.GenresBuilt;
            }
            else
            {
                snapshot.Genres = Genres();
                snapshot.GenresBuilt = DateTime.UtcNow;
            }

            _snapshot = snapshot;
            _visibility.Clear();
            Save(snapshot);
            progress.Report(100);
            _logger.LogInformation(
                "Home page prepared: {Hero} banner titles, {Blocks} libraries with {Rows} rows, {Upcoming} upcoming, {Genres} genres",
                snapshot.Hero.Count,
                snapshot.Blocks.Count,
                snapshot.Blocks.Sum(b => b.Rows.Count),
                snapshot.Upcoming.Count,
                snapshot.Genres.Count);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <inheritdoc />
    public HomePageDto GetPage(User user)
    {
        var snapshot = Current();
        var views = _userViewManager.GetUserViews(new UserViewQuery { User = user });
        var page = new HomePageDto
        {
            Ready = snapshot is not null,
            Built = snapshot?.Built,
            Libraries = views.Select(v => new HomeLibrary
            {
                Id = v.Id,
                Name = v.Name,
                CollectionType = (v as ICollectionFolder)?.CollectionType?.ToString() ?? (v as UserView)?.ViewType?.ToString()
            }).ToList()
        };

        if (snapshot is null)
        {
            return page;
        }

        var visible = Visibility(user, snapshot);

        // Blocks in the order of the user's libraries, only the ones they can open
        var order = views.Select((v, i) => (v.Id, i)).ToDictionary(x => x.Id, x => x.i);
        page.Hero = Only(visible, snapshot.Hero);
        page.Blocks = snapshot.Blocks
            .Where(b => order.ContainsKey(b.LibraryId))
            .OrderBy(b => order[b.LibraryId])
            .Select(b => new HomeBlock
            {
                LibraryId = b.LibraryId,
                Name = b.Name,
                CollectionType = b.CollectionType,
                Rows = Shelves(visible, b.Rows),
                Spotlight = Unwatched(user, visible, b.Spotlight)
            })
            .Where(b => b.Rows.Count > 0)
            .ToList();
        page.Spotlight = Interleave(page.Blocks.Where(IsWatchBlock).Select(b => b.Spotlight)).DistinctBy(c => c.Id).Take(20).ToList();
        page.Upcoming = Releases(visible, snapshot.Upcoming);
        page.Genres = snapshot.Genres;
        return page;
    }

    /// <inheritdoc />
    public HomeLibraryPageDto GetLibraryPage(User user, Guid libraryId)
    {
        var snapshot = Current();
        var page = new HomeLibraryPageDto { Ready = snapshot is not null, Built = snapshot?.Built };
        if (snapshot is null
            || snapshot.Blocks.Find(b => b.LibraryId.Equals(libraryId)) is not { } block
            || !_userViewManager.GetUserViews(new UserViewQuery { User = user }).Any(v => v.Id.Equals(libraryId)))
        {
            return page;
        }

        var visible = Visibility(user, snapshot);
        page.Block = new HomeBlock
        {
            LibraryId = block.LibraryId,
            Name = block.Name,
            CollectionType = block.CollectionType,
            Hero = Only(visible, block.Hero),
            Spotlight = Unwatched(user, visible, block.Spotlight),
            Rows = Shelves(visible, block.Rows),
            Extra = Shelves(visible, block.Extra),
            Upcoming = Releases(visible, block.Upcoming)
        };
        return page;
    }

    /// <inheritdoc />
    public HomePersonalDto GetLibraryPersonal(User user, Guid libraryId)
    {
        var personal = new HomePersonalDto();
        if (_userViewManager.GetUserViews(new UserViewQuery { User = user }).OfType<Folder>().FirstOrDefault(v => v.Id.Equals(libraryId)) is not { } library)
        {
            return personal;
        }

        var type = (library as ICollectionFolder)?.CollectionType;
        if (type is CollectionType.books or CollectionType.music)
        {
            personal.Listening = Progress(user, [MediaType.Audio, MediaType.Book], library);
        }
        else
        {
            personal.Continue = Progress(user, [MediaType.Video], library);

            // Only series (alone or in a mixed library) have a next episode
            if (type is null or CollectionType.tvshows)
            {
                personal.NextUp = NextUp(user, library.Id);
            }
        }

        personal.Favorites = Safe(() => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IsFavorite = true,
            Recursive = true,
            AncestorIds = [library.Id],
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.MusicAlbum, BaseItemKind.Book, BaseItemKind.AudioBook, BaseItemKind.MusicVideo],
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
            Limit = RowSize,
            CollapseBoxSetItems = false,
            EnableTotalRecordCount = false,
            DtoOptions = new DtoOptions(false)
        }).Where(i => i.HasImage(ImageType.Primary, 0)).Select(i => Card(i, withDetails: false)).ToList());

        if (type == CollectionType.music)
        {
            var rows = new List<HomeShelf>();
            AddRow(rows, "mostplayed", "square", Played(user, library, ItemSortBy.PlayCount));
            AddRow(rows, "recentplayed", "square", Played(user, library, ItemSortBy.DatePlayed));
            personal.LibraryRows.Add(new HomeBlock { LibraryId = library.Id, Name = library.Name, Rows = rows });
        }

        return personal;
    }

    /// <summary>The prepared page; the first time (or with a file of an older version) a new one is prepared in the background.</summary>
    private HomeSnapshot? Current()
    {
        var snapshot = Load();
        if ((snapshot is null || snapshot.Version < SnapshotVersion) && Interlocked.Exchange(ref _firstBuildStarted, 1) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await BuildAsync(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Home page could not be prepared");
                    Interlocked.Exchange(ref _firstBuildStarted, 0);
                }
            });
        }

        return snapshot;
    }

    private static List<HomeCard> Only(Func<Guid, bool> visible, IEnumerable<HomeCard> cards) => cards.Where(c => visible(c.Id)).ToList();

    private static List<HomeShelf> Shelves(Func<Guid, bool> visible, IEnumerable<HomeShelf> rows)
        => rows
            .Select(r => new HomeShelf { Key = r.Key, Shape = r.Shape, Items = Only(visible, r.Items) })
            .Where(r => r.Items.Count >= (r.Shape == "rank" ? 3 : 4))
            .ToList();

    private static List<UpcomingRelease> Releases(Func<Guid, bool> visible, IEnumerable<UpcomingRelease> releases)
        => releases.Where(r => (r.Kind == "Episode" ? r.SeriesId : r.RelatedItemId) is Guid id && visible(id)).ToList();

    /// <summary>"What do we watch today?": movies already watched are left out (cheap: a dozen titles).</summary>
    private List<HomeCard> Unwatched(User user, Func<Guid, bool> visible, IEnumerable<HomeCard> cards)
        => cards
            .Where(c => visible(c.Id))
            .Where(c => c.Type != nameof(BaseItemKind.Movie)
                || _libraryManager.GetItemById(c.Id) is not { } item
                || _userDataManager.GetUserData(user, item) is not { Played: true })
            .ToList();

    /// <inheritdoc />
    public HomePersonalDto GetPersonal(User user)
    {
        var personal = new HomePersonalDto
        {
            Continue = Progress(user, [MediaType.Video], null),
            NextUp = NextUp(user),
            Listening = Progress(user, [MediaType.Audio, MediaType.Book], null),
            Favorites = Safe(() => _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IsFavorite = true,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.MusicAlbum, BaseItemKind.Book, BaseItemKind.AudioBook],
                OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
                Limit = RowSize,
                CollapseBoxSetItems = false,
                EnableTotalRecordCount = false,
                DtoOptions = new DtoOptions(false)
            }).Where(i => i.HasImage(ImageType.Primary, 0)).Select(i => Card(i, withDetails: false)).ToList())
        };

        // Rows of the user inside the library blocks: music played and books being read
        foreach (var view in _userViewManager.GetUserViews(new UserViewQuery { User = user }).OfType<Folder>())
        {
            var type = (view as ICollectionFolder)?.CollectionType ?? (view as UserView)?.ViewType;
            var rows = new List<HomeShelf>();
            if (type == CollectionType.music)
            {
                AddRow(rows, "mostplayed", "square", Played(user, view, ItemSortBy.PlayCount));
                AddRow(rows, "recentplayed", "square", Played(user, view, ItemSortBy.DatePlayed));
            }

            if (rows.Count > 0)
            {
                personal.LibraryRows.Add(new HomeBlock { LibraryId = view.Id, Name = view.Name, Rows = rows });
            }
        }

        return personal;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemUpdated -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
        _recentTimer.Dispose();
        _buildLock.Dispose();
    }

    /// <summary>A short pause between steps, so the server keeps answering the apps while the page is prepared.</summary>
    private static Task Breathe(CancellationToken cancellationToken) => Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

    private T Safe<T>(Func<T> read)
        where T : new()
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Home row could not be read");
            return new T();
        }
    }

    /* ---------- Library blocks ---------- */

    private IEnumerable<CollectionFolder> Libraries()
        => _libraryManager.GetUserRootFolder().Children.OfType<CollectionFolder>()
            .Where(f => f.CollectionType is not (CollectionType.boxsets or CollectionType.playlists or CollectionType.livetv));

    private async Task<HomeBlock> BuildBlockAsync(CollectionFolder library, CancellationToken cancellationToken)
    {
        var block = new HomeBlock { LibraryId = library.Id, Name = library.Name, CollectionType = library.CollectionType?.ToString() };
        var rows = block.Rows;
        List<BaseItem> topMovies = [];
        List<BaseItem> topSeries = [];

        try
        {
            switch (library.CollectionType)
            {
                case CollectionType.movies:
                    block.Spotlight = Picks(library, [BaseItemKind.Movie]);
                    AddRow(rows, "top10movies", "rank", topMovies = await Top10Async(false, library, cancellationToken).ConfigureAwait(false));
                    AddRecentRow(rows, library, "latestmovies");
                    AddRecentRow(rows, library, "releases");
                    AddRow(rows, "toprated", "poster", BestRated(library, [BaseItemKind.Movie], 25000));
                    AddRow(rows, "gems", "poster", Gems(library));
                    AddRow(rows, "classics", "poster", Items(library, [BaseItemKind.Movie], ItemSortBy.CommunityRating, q =>
                    {
                        q.MaxPremiereDate = new DateTime(1991, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                        q.MinCommunityRating = 7;
                    }));
                    break;

                case CollectionType.tvshows:
                    block.Spotlight = Picks(library, [BaseItemKind.Series]);
                    AddRow(rows, "top10series", "rank", topSeries = await Top10Async(true, library, cancellationToken).ConfigureAwait(false));
                    AddRecentRow(rows, library, "newepisodes");
                    AddRecentRow(rows, library, "newseries");
                    AddRecentRow(rows, library, "onair");
                    AddRow(rows, "toprated", "poster", BestRated(library, [BaseItemKind.Series], 20000));
                    AddRow(rows, "marathon", "poster", Items(library, [BaseItemKind.Series], ItemSortBy.Random, q =>
                    {
                        q.SeriesStatuses = [SeriesStatus.Ended];
                        q.MinCommunityRating = 7.5;
                    }));
                    break;

                case CollectionType.music:
                    AddRecentRow(rows, library, "newalbums");
                    AddRow(rows, "artists", "round", Artists(library));
                    AddRow(rows, "discoveralbums", "square", Items(library, [BaseItemKind.MusicAlbum], ItemSortBy.Random));
                    break;

                case CollectionType.books:
                    block.Spotlight = Items(library, [BaseItemKind.Book, BaseItemKind.AudioBook, BaseItemKind.MusicAlbum], ItemSortBy.Random, q => q.IsFolder = null, 40)
                        .Where(i => i.HasImage(ImageType.Primary, 0) && !string.IsNullOrEmpty(i.Overview))
                        .Take(SpotlightSize)
                        .Select(i => Card(i, withDetails: true))
                        .ToList();
                    AddRecentRow(rows, library, "latestbooks");
                    AddRow(rows, "discoverbooks", "poster", Items(library, [BaseItemKind.Book, BaseItemKind.AudioBook, BaseItemKind.MusicAlbum], ItemSortBy.Random));
                    break;

                case CollectionType.musicvideos:
                    AddRecentRow(rows, library, "latest");
                    AddRow(rows, "discovervideos", "wide", Items(library, [BaseItemKind.MusicVideo], ItemSortBy.Random));
                    break;

                case CollectionType.homevideos:
                    AddRecentRow(rows, library, "latest");
                    break;

                case CollectionType.photos:
                    AddRecentRow(rows, library, "latest");
                    break;

                default:
                    // Mixed libraries (anime, kids): movies and series, each top 10 apart
                    block.Spotlight = Interleave([Picks(library, [BaseItemKind.Movie]), Picks(library, [BaseItemKind.Series])]).Take(SpotlightSize).ToList();
                    AddRow(rows, "top10movies", "rank", topMovies = await Top10Async(false, library, cancellationToken).ConfigureAwait(false));
                    AddRow(rows, "top10series", "rank", topSeries = await Top10Async(true, library, cancellationToken).ConfigureAwait(false));
                    AddRecentRow(rows, library, "latest");
                    AddRecentRow(rows, library, "releases");
                    AddRecentRow(rows, library, "onair");
                    AddRow(rows, "toprated", "poster", BestRated(library, [BaseItemKind.Movie, BaseItemKind.Series], 10000));
                    AddRecentRow(rows, library, "movies");
                    break;
            }

            LibraryPageParts(block, library, topMovies, topSeries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Home block of {Library} could not be prepared", library.Name);
        }

        return block;
    }

    /// <summary>
    /// What only the page of a library shows: its own banner (movies and series: their top 10, completed with the
    /// "what do we watch today?" picks; music: artists with a backdrop) and three of its biggest genres, a
    /// different set each day.
    /// </summary>
    private void LibraryPageParts(HomeBlock block, Folder library, List<BaseItem> topMovies, List<BaseItem> topSeries)
    {
        static List<BaseItem> WithBackdrop(IEnumerable<BaseItem> items) => items.Where(i => i.HasImage(ImageType.Backdrop, 0)).ToList();

        if (IsWatchBlock(block) && block.CollectionType is not ("musicvideos" or "homevideos" or "photos"))
        {
            block.Hero = Interleave([WithBackdrop(topMovies), WithBackdrop(topSeries)])
                .Select(i => Card(i, withDetails: true))
                .Concat(block.Spotlight)
                .DistinctBy(c => c.Id)
                .Take(HeroSize)
                .ToList();
        }
        else if (block.CollectionType == "music")
        {
            block.Hero = WithBackdrop(Artists(library))
                .Where(a => a.HasImage(ImageType.Primary, 0))
                .Take(HeroSize)
                .Select(a => Card(a, withDetails: true))
                .ToList();
        }

        // What the genres are counted on, how their rows are drawn, and whether only well rated titles go in
        (BaseItemKind[] Kinds, string Shape, bool Rated) genreRows = block.CollectionType switch
        {
            "movies" => ([BaseItemKind.Movie], "poster", true),
            "tvshows" => ([BaseItemKind.Series], "poster", true),
            "music" => ([BaseItemKind.MusicAlbum], "square", false),
            "books" => ([BaseItemKind.Book, BaseItemKind.AudioBook, BaseItemKind.MusicAlbum], "poster", false),
            "musicvideos" => ([BaseItemKind.MusicVideo], "wide", false),
            "homevideos" or "photos" => ([], string.Empty, false),
            _ => ([BaseItemKind.Movie, BaseItemKind.Series], "poster", true)
        };

        if (genreRows.Kinds.Length == 0)
        {
            return;
        }

        // The genres of a sample of the library are counted (counting the whole library is heavy)
        var seed = DateTime.UtcNow.DayOfYear;
        var genres = Items(library, genreRows.Kinds, ItemSortBy.Random, null, 300)
            .SelectMany(i => i.Genres)
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(9)
            .Select(g => g.Key)
            .OrderBy(g => (StringComparer.OrdinalIgnoreCase.GetHashCode(g) ^ seed) & 0xffff)
            .Take(3)
            .ToList();

        foreach (var genre in genres)
        {
            AddRow(block.Extra, "genre:" + genre, genreRows.Shape, Items(library, genreRows.Kinds, ItemSortBy.Random, q =>
            {
                q.Genres = [genre];
                if (genreRows.Rated)
                {
                    q.MinCommunityRating = 6;
                }
            }));
        }
    }

    private async Task<List<BaseItem>> Top10Async(bool series, Folder library, CancellationToken cancellationToken)
    {
        try
        {
            var ids = await _discovery.GetTop10IdsAsync(series, library.Id, cancellationToken).ConfigureAwait(false);
            return ids.Select(id => _libraryManager.GetItemById(id)).OfType<BaseItem>().ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Top 10 of {Library} not available", library.Name);
            return [];
        }
    }

    /// <summary>"What do we watch today?": well rated titles with a backdrop, a different handful each time.</summary>
    private List<HomeCard> Picks(Folder library, BaseItemKind[] kinds)
        => Items(library, kinds, ItemSortBy.Random, q => q.MinCommunityRating = 7, 60)
            .Where(i => i.HasImage(ImageType.Backdrop, 0) && i.HasImage(ImageType.Primary, 0))
            .Take(SpotlightSize)
            .Select(i => Card(i, withDetails: true))
            .ToList();

    /// <summary>Movies and series libraries (the general "what do we watch today?" and the banner leave books out).</summary>
    private static bool IsWatchBlock(HomeBlock block) => block.CollectionType is not ("books" or "music");

    /// <summary>Takes one of each list in turn (so movies and series, or libraries, alternate).</summary>
    private static IEnumerable<T> Interleave<T>(IEnumerable<List<T>> lists)
    {
        var all = lists.ToList();
        var longest = all.Count == 0 ? 0 : all.Max(l => l.Count);
        for (var i = 0; i < longest; i++)
        {
            foreach (var list in all)
            {
                if (i < list.Count)
                {
                    yield return list[i];
                }
            }
        }
    }

    private static void Released(InternalItemsQuery query, DateTime since)
    {
        query.MinPremiereDate = since;
        query.MaxPremiereDate = DateTime.UtcNow;
    }

    private IReadOnlyList<BaseItem> Items(Folder library, BaseItemKind[] kinds, ItemSortBy sort, Action<InternalItemsQuery>? extra = null, int limit = RowSize + 6)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = kinds,
            Recursive = true,
            IsVirtualItem = false,
            CollapseBoxSetItems = false,
            EnableTotalRecordCount = false,
            Limit = limit,
            OrderBy = [(sort, sort == ItemSortBy.Random ? SortOrder.Ascending : SortOrder.Descending)],
            DtoOptions = new DtoOptions(false)
        };
        extra?.Invoke(query);
        return library.GetItemList(query);
    }

    /// <summary>Best rated by IMDb (rating weighted by votes), falling back to the community rating.</summary>
    private IEnumerable<BaseItem> BestRated(Folder library, BaseItemKind[] kinds, int minVotes)
        => Items(library, kinds, ItemSortBy.CommunityRating, q => q.MinCommunityRating = 7.2, 80)
            .OrderByDescending(i => _imdb.GetRating(i) is { Votes: > 0 } r
                ? (r.Votes / (double)(r.Votes + minVotes) * r.Rating) + (minVotes / (double)(r.Votes + minVotes) * 6.8)
                : (i.CommunityRating ?? 0) * 0.9);

    /// <summary>Well rated movies that few people know (IMDb), a different handful each day.</summary>
    private IEnumerable<BaseItem> Gems(Folder library)
    {
        var seed = DateTime.UtcNow.DayOfYear;
        return Items(library, [BaseItemKind.Movie], ItemSortBy.Random, q => q.MinCommunityRating = 6.8, 150)
            .Where(i => _imdb.GetRating(i) is { Rating: >= 7f, Votes: >= 1500 and <= 60000 })
            .OrderBy(i => (i.Id.GetHashCode() ^ seed) & 0xffff);
    }

    /// <summary>Newest movies and series together: a series counts from its newest episode.</summary>
    private IEnumerable<BaseItem> Newest(Folder library)
        => Items(library, [BaseItemKind.Movie], ItemSortBy.DateCreated)
            .Select(m => (Item: m, Added: m.DateCreated))
            .Concat(LatestEpisodes(library))
            .OrderByDescending(x => x.Added)
            .Select(x => x.Item);

    /// <summary>
    /// The series with the episodes added last, newest first, each with the date its newest episode came in.
    /// The dates of a series itself only change on a full refresh, so the episodes are asked directly.
    /// </summary>
    private List<(BaseItem Item, DateTime Added)> LatestEpisodes(Folder library)
        => SeriesOf(Items(library, [BaseItemKind.Episode], ItemSortBy.DateCreated, null, 400), e => e.DateCreated);

    /// <summary>"New series": the series whose newest season premiered in the last year, the most recent season first.</summary>
    private IEnumerable<BaseItem> NewSeries(Folder library)
    {
        var since = DateTime.UtcNow.AddYears(-1);
        var seasons = Items(library, [BaseItemKind.Season], ItemSortBy.PremiereDate, q => Released(q, since), 300)
            .OfType<Season>()
            .Where(s => s.Series is not null)
            .Select(s => (Item: (BaseItem)s.Series, Date: s.PremiereDate ?? DateTime.MinValue));

        // Series whose seasons have no premiere date yet count from the date of the series
        var series = Items(library, [BaseItemKind.Series], ItemSortBy.PremiereDate, q => Released(q, since))
            .Select(s => (Item: s, Date: s.PremiereDate ?? DateTime.MinValue));

        return seasons.Concat(series)
            .DistinctBy(x => x.Item.Id)
            .OrderByDescending(x => x.Date)
            .Select(x => x.Item);
    }

    /// <summary>"On air": the continuing series, the one with the most recently aired episode first.</summary>
    private IEnumerable<BaseItem> OnAir(Folder library)
        => SeriesOf(Items(library, [BaseItemKind.Episode], ItemSortBy.PremiereDate, q => q.MaxPremiereDate = DateTime.UtcNow, 600), e => e.PremiereDate ?? DateTime.MinValue)
            .Select(x => x.Item)
            .Where(s => s is Series { Status: SeriesStatus.Continuing });

    /// <summary>The series of a list of episodes, in the order of the episodes, each once.</summary>
    private List<(BaseItem Item, DateTime Added)> SeriesOf(IEnumerable<BaseItem> episodes, Func<Episode, DateTime> date)
    {
        var result = new List<(BaseItem Item, DateTime Added)>();
        var seen = new HashSet<Guid>();
        foreach (var episode in episodes.OfType<Episode>())
        {
            if (episode.SeriesId == Guid.Empty || !seen.Add(episode.SeriesId) || _libraryManager.GetItemById(episode.SeriesId) is not Series series)
            {
                continue;
            }

            result.Add((series, date(episode)));
            if (result.Count >= RowSize * 2)
            {
                break;
            }
        }

        return result;
    }

    private IEnumerable<BaseItem> Artists(Folder library)
    {
        var result = _libraryManager.GetAlbumArtists(new InternalItemsQuery
        {
            AncestorIds = [library.Id],
            Recursive = true,
            OrderBy = [(ItemSortBy.Random, SortOrder.Ascending)],
            Limit = 60,
            EnableTotalRecordCount = false,
            DtoOptions = new DtoOptions(false)
        });
        return result.Items.Select(r => r.Item);
    }

    private IReadOnlyList<BaseItem> Played(User user, Folder library, ItemSortBy sort)
        => Safe<List<BaseItem>>(() => [.. _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            AncestorIds = [library.Id],
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true,
            IsPlayed = true,
            OrderBy = [(sort, SortOrder.Descending)],
            Limit = RowSize,
            EnableTotalRecordCount = false,
            DtoOptions = new DtoOptions(false)
        })]);

    private void AddRow(List<HomeShelf> rows, string key, string shape, IEnumerable<BaseItem> items)
    {
        var cards = items
            .Where(i => i.HasImage(ImageType.Primary, 0))
            .DistinctBy(i => i.Id)
            .Take(shape == "rank" ? 10 : RowSize)
            .Select(i => Card(i, withDetails: false))
            .ToList();

        if (cards.Count >= (shape == "rank" ? 3 : 4))
        {
            rows.Add(new HomeShelf { Key = key, Shape = shape, Items = cards });
        }
    }

    private void AddRecentRow(List<HomeShelf> rows, CollectionFolder library, string key)
    {
        var (shape, items) = RecentRow(library, key);
        AddRow(rows, key, shape, items);
    }

    /// <summary>The rows of what is new in a library: what each one holds and how it is drawn.</summary>
    private (string Shape, IEnumerable<BaseItem> Items) RecentRow(CollectionFolder library, string key)
    {
        var yearAgo = DateTime.UtcNow.AddYears(-1);
        return (library.CollectionType, key) switch
        {
            (CollectionType.movies, "latestmovies") => ("poster", Items(library, [BaseItemKind.Movie], ItemSortBy.DateCreated)),
            (CollectionType.movies, "releases") => ("poster", Items(library, [BaseItemKind.Movie], ItemSortBy.PremiereDate, q => Released(q, yearAgo))),
            (CollectionType.tvshows, "newepisodes") => ("poster", LatestEpisodes(library).Select(x => x.Item)),
            (CollectionType.tvshows, "newseries") => ("poster", NewSeries(library)),
            (_, "onair") => ("poster", OnAir(library)),
            (CollectionType.music, "newalbums") => ("square", Items(library, [BaseItemKind.MusicAlbum], ItemSortBy.DateCreated)),
            (CollectionType.books, "latestbooks") => ("poster", Items(library, [BaseItemKind.Book, BaseItemKind.AudioBook, BaseItemKind.MusicAlbum], ItemSortBy.DateCreated, q => q.IsFolder = null)),
            (CollectionType.homevideos or CollectionType.musicvideos, "latest") => ("wide", Items(library, [BaseItemKind.Video, BaseItemKind.MusicVideo, BaseItemKind.Movie], ItemSortBy.DateCreated)),
            (CollectionType.photos, "latest") => ("square", Items(library, [BaseItemKind.Photo, BaseItemKind.PhotoAlbum], ItemSortBy.DateCreated)),
            (_, "latest") => ("poster", Newest(library)),
            (_, "releases") => ("poster", Items(library, [BaseItemKind.Movie, BaseItemKind.Series], ItemSortBy.PremiereDate, q => Released(q, yearAgo))),
            (_, "movies") => ("poster", Items(library, [BaseItemKind.Movie], ItemSortBy.PremiereDate)),
            _ => ("poster", [])
        };
    }

    /// <summary>The order of the rows of a library (the full preparation adds them in this order).</summary>
    private static string[] RowOrder(string? collectionType) => collectionType switch
    {
        "movies" => ["top10movies", "latestmovies", "releases", "toprated", "gems", "classics"],
        "tvshows" => ["top10series", "newepisodes", "newseries", "onair", "toprated", "marathon"],
        "music" => ["newalbums", "artists", "discoveralbums"],
        "books" => ["latestbooks", "discoverbooks"],
        "musicvideos" => ["latest", "discovervideos"],
        "homevideos" or "photos" => ["latest"],
        _ => ["top10movies", "top10series", "latest", "releases", "onair", "toprated", "movies"]
    };

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Video or Series or Season or MusicAlbum or Audio or Book or Photo)
        {
            // Many changes come together (a scan, a whole season): wait until they stop
            _recentTimer.Change(_recentDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Prepares again only the rows of what is new, so an item just added shows up in a minute. The rest of the
    /// page (banner, top 10, genres) stays as the scheduled task left it.
    /// </summary>
    private async Task RefreshRecentRowsAsync()
    {
        if (Load() is not { } snapshot)
        {
            return;
        }

        // The full preparation is running: try again a little later
        if (!await _buildLock.WaitAsync(0).ConfigureAwait(false))
        {
            _recentTimer.Change(_recentDelay, Timeout.InfiniteTimeSpan);
            return;
        }

        try
        {
            var libraries = Libraries().ToDictionary(l => l.Id);
            foreach (var block in snapshot.Blocks)
            {
                if (!libraries.TryGetValue(block.LibraryId, out var library))
                {
                    continue;
                }

                var fresh = new List<HomeShelf>();
                foreach (var key in RowOrder(block.CollectionType).Where(_recentKeys.Contains))
                {
                    AddRecentRow(fresh, library, key);
                }

                // Same order as before; the list is replaced at once, so a visit never sees it half done
                var old = block.Rows;
                block.Rows = RowOrder(block.CollectionType)
                    .Select(key => _recentKeys.Contains(key) ? fresh.Find(r => r.Key == key) : old.Find(r => r.Key == key))
                    .OfType<HomeShelf>()
                    .ToList();
            }

            Save(snapshot);
            _logger.LogInformation("Home page: rows of what is new prepared again after library changes");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rows of what is new could not be prepared again");
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /* ---------- Rows of the user ---------- */

    /// <summary>Whether the user can see a title (parental rating, library access), remembered for a while.</summary>
    private Func<Guid, bool> Visibility(User user, HomeSnapshot snapshot)
    {
        var now = DateTime.UtcNow;
        var entry = _visibility.AddOrUpdate(
            user.Id,
            _ => (now, snapshot.Built, new ConcurrentDictionary<Guid, bool>()),
            (_, old) => old.Built == snapshot.Built && now - old.At < _visibilityMaxAge ? old : (now, snapshot.Built, new ConcurrentDictionary<Guid, bool>()));

        return id => entry.Visible.GetOrAdd(id, key => _libraryManager.GetItemById(key) is { } item && item.IsVisibleStandalone(user));
    }

    private List<HomeProgressCard> Progress(User user, MediaType[] mediaTypes, Folder? library)
        => Safe(() => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            IsResumable = true,
            Limit = 12,
            Recursive = true,
            MediaTypes = mediaTypes,
            AncestorIds = library is null ? [] : [library.Id],
            IsVirtualItem = false,
            CollapseBoxSetItems = false,
            EnableTotalRecordCount = false,
            DtoOptions = new DtoOptions(false)
        }).Select(i => ProgressCard(user, i)).ToList());

    private List<HomeProgressCard> NextUp(User user, Guid? parentId = null)
        => Safe(() => _tvSeriesManager.GetNextUp(
                new NextUpQuery { User = user, ParentId = parentId, Limit = 12, EnableResumable = false, EnableRewatching = false, EnableTotalRecordCount = false },
                new DtoOptions(false))
            .Items.Select(i => ProgressCard(user, i)).ToList());

    private HomeProgressCard ProgressCard(User user, BaseItem item)
    {
        var data = _userDataManager.GetUserData(user, item);
        var runtime = item.RunTimeTicks ?? 0;
        var position = data?.PlaybackPositionTicks ?? 0;
        var card = new HomeProgressCard
        {
            Id = item.Id,
            Name = item.Name ?? string.Empty,
            Type = item.GetBaseItemKind().ToString(),
            Progress = runtime > 0 ? Math.Clamp(position * 100.0 / runtime, 0, 100) : 0,
            RemainingTicks = runtime > 0 && position > 0 ? runtime - position : null,
            PositionTicks = position
        };

        switch (item)
        {
            case Episode episode:
                var series = episode.Series;
                card.Name = episode.SeriesName ?? card.Name;
                card.SeriesId = series?.Id;
                card.Subtitle = string.Create(
                    CultureInfo.InvariantCulture,
                    $"T{episode.ParentIndexNumber ?? 0}:E{episode.IndexNumber ?? 0} · {episode.Name}");
                SetImage(card, (episode, ImageType.Primary), (series, ImageType.Thumb), (series, ImageType.Backdrop), (series, ImageType.Primary));
                break;
            case Audio audio:
                var album = audio.AlbumEntity;
                card.Name = album?.Name ?? audio.Name ?? string.Empty;
                card.Subtitle = audio.AlbumArtists.FirstOrDefault() ?? audio.Name;
                SetImage(card, (audio, ImageType.Primary), (album, ImageType.Primary));
                break;
            default:
                SetImage(card, (item, ImageType.Thumb), (item, ImageType.Backdrop), (item, ImageType.Primary));
                break;
        }

        return card;
    }

    private void SetImage(HomeProgressCard card, params (BaseItem? Item, ImageType Type)[] choices)
    {
        foreach (var (item, type) in choices)
        {
            if (item?.GetImageInfo(type, 0) is { } info)
            {
                card.ImageItemId = item.Id;
                card.ImageType = type.ToString();
                card.ImageTag = _imageProcessor.GetImageCacheTag(item, info);
                return;
            }
        }
    }

    /* ---------- Cards, genres, disk ---------- */

    private HomeCard Card(BaseItem item, bool withDetails)
    {
        string? Tag(ImageType type) => item.GetImageInfo(type, 0) is { } info ? _imageProcessor.GetImageCacheTag(item, info) : null;

        var card = new HomeCard
        {
            Id = item.Id,
            Name = item.Name ?? string.Empty,
            Type = item.GetBaseItemKind().ToString(),
            Year = item.ProductionYear,
            Rating = _imdb.GetRating(item) is { Votes: > 0 } imdb ? imdb.Rating : item.CommunityRating,
            PrimaryTag = Tag(ImageType.Primary),
            Note = item is Series { Status: SeriesStatus.Continuing } ? "Continuing" : null
        };

        if (withDetails)
        {
            card.BackdropTag = Tag(ImageType.Backdrop);
            card.LogoTag = Tag(ImageType.Logo);
            card.Overview = item.Overview;
            card.Genres = item.Genres.Take(3).ToArray();
        }

        return card;
    }

    private List<HomeGenre> Genres()
        => Safe(() => _libraryManager.GetGenres(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                Recursive = true,
                DtoOptions = new DtoOptions { Fields = [ItemFields.ItemCounts] }
            })
            .Items
            .Select(g => new HomeGenre { Id = g.Item.Id, Name = g.Item.Name, Count = (g.ItemCounts?.MovieCount ?? 0) + (g.ItemCounts?.SeriesCount ?? 0) })
            .Where(g => g.Count > 0)
            .OrderByDescending(g => g.Count)
            .Take(24)
            .ToList());

    private HomeSnapshot? Load()
    {
        if (_snapshot is not null || Interlocked.Exchange(ref _loaded, 1) == 1)
        {
            return _snapshot;
        }

        try
        {
            if (File.Exists(SnapshotPath))
            {
                var saved = JsonSerializer.Deserialize<HomeSnapshot>(File.ReadAllText(SnapshotPath));

                // A file of the first version (rows mixing libraries) is prepared again
                _snapshot ??= saved is { Blocks.Count: > 0 } ? saved : null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saved home page could not be read");
        }

        return _snapshot;
    }

    private void Save(HomeSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
            var temp = SnapshotPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
            File.Move(temp, SnapshotPath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Home page could not be saved");
        }
    }
}
