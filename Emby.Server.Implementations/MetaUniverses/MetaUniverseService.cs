using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Data.Events;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MetaUniverses;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MetaUniverses;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.MetaUniverses;

/// <summary>
/// Builds the meta universes from built-in franchise rules, TMDb collections found in the libraries and the
/// changes made by administrators (stored in metauniverses.json in the data folder). They are prepared by a scheduled
/// task and kept in a file, so opening them never waits for a build.
/// </summary>
public sealed partial class MetaUniverseService : IMetaUniverseService, IDisposable
{
    private const string SourceBuiltin = "builtin";
    private const string SourceAuto = "auto";
    private const string SourceCustom = "custom";
    private const int MinAutoMovies = 3;
    private const int SnapshotVersion = 1;
    private const int BatchSize = 500;
    private const int MaxStoredPeople = 100;
    private const string KindMovie = "movie";
    private const string KindSeries = "series";
    private const string KindAlbum = "album";
    private const string KindCollection = "collection";
    private const string KindOther = "other";
    private static readonly TimeSpan _maxAge = TimeSpan.FromHours(6);

    private static readonly BaseItemKind[] _kinds =
    {
        BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.MusicAlbum, BaseItemKind.BoxSet, BaseItemKind.Book, BaseItemKind.MusicVideo
    };

    private static readonly string[] _soundtrackGenres = { "soundtrack", "soundtracks", "banda sonora", "bandas sonoras", "score", "film score", "stage & screen" };

    private static readonly IReadOnlyList<MetaUniverseDefinition> _builtin = new[]
    {
        Seed("mcu", "Universo Cinematográfico de Marvel", "Las películas y series de Marvel Studios conectadas en un mismo universo.", new[] { "marvel cinematic universe (mcu)", "marvel cinematic universe" }, new[] { "Marvel Studios" }, Array.Empty<string>()),
        Seed("dc", "Universo DC", "Superhéroes y villanos de DC Comics en cine y televisión.", new[] { "dc extended universe (dceu)", "dc universe (dcu)", "dc comics", "based on dc comics" }, new[] { "DC Studios", "DC Films", "DC Entertainment", "DC Comics" }, Array.Empty<string>()),
        Seed("starwars", "Star Wars", "Hace mucho tiempo, en una galaxia muy, muy lejana…", new[] { "star wars" }, Array.Empty<string>(), new[] { "star wars", @"^andor$", "mandalorian", "mandaloriano", @"obi-?wan", @"^ahsoka", "rogue one", "boba fett", @"^the acolyte", @"^the bad batch", "la remesa mala", "skeleton crew", "clone wars" }),
        Seed("wizarding", "Mundo Mágico", "Harry Potter, Animales Fantásticos y todo el mundo mágico.", new[] { "wizarding world" }, Array.Empty<string>(), new[] { "harry potter", "fantastic beasts", "animales fant[aá]sticos" }),
        Seed("middleearth", "La Tierra Media", "El universo de Tolkien: El Hobbit, El Señor de los Anillos y más.", new[] { "middle-earth (tolkien)" }, Array.Empty<string>(), new[] { "lord of the rings", "se[ñn]or de los anillos", @"\bhobbit\b", "rings of power", "anillos de poder", "rohirrim" }),
        Seed("monsterverse", "Monsterverse", "Godzilla, Kong y los titanes de Monarch.", new[] { "monsterverse" }, Array.Empty<string>(), new[] { "godzilla", "king kong", @"^kong\b", @"^monarch:" }),
        Seed("jurassic", "Jurassic Park y Jurassic World", "Dinosaurios que vuelven a la vida.", Array.Empty<string>(), Array.Empty<string>(), new[] { "jurassic", "jur[aá]sic[oa]" }),
        Seed("fastfurious", "Rápidos y Furiosos", "La familia Toretto y sus carreras.", Array.Empty<string>(), Array.Empty<string>(), new[] { @"fast (and|&) (the )?furious", @"^furious ?\d", @"^fast (five|x)\b", @"r[aá]pidos? y furiosos?", @"hobbs (and|&|y) shaw", "2 fast 2 furious", "tokyo drift", "reto tokio" }),
        Seed("startrek", "Star Trek", "El espacio, la última frontera.", Array.Empty<string>(), Array.Empty<string>(), new[] { "star trek" }),
        Seed("alien", "Alien y Depredador", "Xenomorfos, Yautjas y la corporación Weyland-Yutani.", new[] { "xenomorph" }, Array.Empty<string>(), new[] { @"^aliens?($|[:,\-]| \d| vs\.?| el | resurrection)", "^prometheus", "predator", "depredador", @"^avp\b" }),
        Seed("conjuring", "Universo El Conjuro", "Los casos de Ed y Lorraine Warren y sus demonios.", Array.Empty<string>(), Array.Empty<string>(), new[] { "conjuring", "el conjuro", "annabelle", @"^the nun\b", @"^la monja\b", "llorona" }),
        Seed("bond", "James Bond 007", "Con licencia para matar.", new[] { "james bond" }, Array.Empty<string>(), new[] { @"\b007\b" }),
        Seed("xmen", "X-Men", "Mutantes, la Patrulla-X y Deadpool.", new[] { "x-men" }, Array.Empty<string>(), new[] { @"x-?men", "wolverine", "^logan$", "deadpool", "new mutants", "nuevos mutantes", "dark phoenix", "f[eé]nix oscura" }),
        Seed("spiderverse", "Spider-Man y su universo", "El Hombre Araña, sus versiones y sus villanos.", Array.Empty<string>(), Array.Empty<string>(), new[] { @"spider-?man", "hombre ara[ñn]a", @"spider-?verse", "^venom", "^morbius", "^madame web", "^kraven" }),
        Seed("transformers", "Transformers", "Autobots contra Decepticons.", Array.Empty<string>(), Array.Empty<string>(), new[] { "transformers", "^bumblebee" }),
        Seed("westeros", "Poniente", "Juego de Tronos, La Casa del Dragón y los Siete Reinos.", Array.Empty<string>(), Array.Empty<string>(), new[] { "game of thrones", "juego de tronos", "house of the dragon", "casa del drag[oó]n", "seven kingdoms", "siete reinos" }),
        Seed("breakingbad", "Breaking Bad", "Walter White, Jesse Pinkman y Saul Goodman.", Array.Empty<string>(), Array.Empty<string>(), new[] { "breaking bad", "better call saul" }),
        Seed("walkingdead", "The Walking Dead", "El apocalipsis zombi y sus supervivientes.", Array.Empty<string>(), Array.Empty<string>(), new[] { "walking dead" }),
        Seed("avatar", "Avatar: La Leyenda de Aang", "Los maestros de los cuatro elementos.", Array.Empty<string>(), Array.Empty<string>(), new[] { "last airbender", "[uú]ltimo maestro (del )?aire", "leyenda de aang", "korra" }),
        Seed("dragonball", "Dragon Ball", "Goku y las esferas del dragón.", Array.Empty<string>(), Array.Empty<string>(), new[] { "dragon ?ball" }),
        Seed("pokemon", "Pokémon", "Atrápalos a todos.", Array.Empty<string>(), Array.Empty<string>(), new[] { "pok[eé]mon" }),
        Seed("shrek", "Shrek", "Muy, muy lejano.", Array.Empty<string>(), Array.Empty<string>(), new[] { "shrek", "puss in boots", "gato con botas" })
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<MetaUniverseService> _logger;
    private readonly string _storePath;
    private readonly string _snapshotPath;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private readonly ConcurrentDictionary<(Guid User, DateTime Built), Lazy<HashSet<Guid>>> _visibility = new();
    private readonly ConcurrentDictionary<string, Regex?> _regexCache = new(StringComparer.Ordinal);

    private Dictionary<string, MetaUniverseDefinition>? _stored;
    private volatile Snapshot? _snapshot;
    private volatile bool _dirty;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetaUniverseService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public MetaUniverseService(ILibraryManager libraryManager, IUserManager userManager, IApplicationPaths appPaths, ILogger<MetaUniverseService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
        _storePath = Path.Combine(appPaths.DataPath, "metauniverses.json");
        _snapshotPath = Path.Combine(appPaths.DataPath, "metauniverses.snapshot.json");
        _userManager.OnUserUpdated += OnUserUpdated;
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemUpdated += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
    }

    /// <inheritdoc />
    public IReadOnlyList<MetaUniverseSummary> GetUniverses(User? user, bool includeHidden)
    {
        var snapshot = GetSnapshot();
        var visible = VisibleIds(user, snapshot);
        var result = new List<MetaUniverseSummary>();
        foreach (var universe in snapshot.Universes)
        {
            if (universe.Definition.Hidden && !includeHidden)
            {
                continue;
            }

            var summary = Summarize(universe.Definition, Visible(universe.Members, visible));
            var total = summary.MovieCount + summary.SeriesCount + summary.AlbumCount;
            if (!includeHidden && (total < 2 || summary.MovieCount + summary.SeriesCount == 0))
            {
                continue;
            }

            result.Add(summary);
        }

        return result
            .OrderByDescending(s => s.MovieCount + s.SeriesCount)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public MetaUniverseDetails? GetUniverse(string id, User? user)
    {
        var snapshot = GetSnapshot();
        var universe = snapshot.Universes.FirstOrDefault(u => string.Equals(u.Definition.Id, id, StringComparison.OrdinalIgnoreCase));
        if (universe is null)
        {
            return null;
        }

        var items = Visible(universe.Members, VisibleIds(user, snapshot));
        var def = universe.Definition;
        var watchable = items.Where(IsWatchable).ToList();
        var release = watchable.OrderBy(When).ThenBy(i => i.SortName, StringComparer.Ordinal).Select(i => i.Id).ToList();

        var known = watchable.Select(i => i.Id).ToHashSet();
        var chrono = def.ChronologicalOrder.Where(known.Contains).Distinct().ToList();
        var hasChronology = chrono.Count > 0;
        chrono.AddRange(release.Where(i => !chrono.Contains(i)));

        return new MetaUniverseDetails
        {
            Summary = Summarize(def, items),
            Definition = def,
            ReleaseOrder = release,
            ChronologicalOrder = chrono,
            HasChronology = hasChronology,
            MovieIds = IdsOf(items, KindMovie),
            SeriesIds = IdsOf(items, KindSeries),
            AlbumIds = IdsOf(items, KindAlbum),
            CollectionIds = items.Where(i => i.Kind == KindCollection).OrderBy(i => i.SortName, StringComparer.Ordinal).Select(i => i.Id).ToList(),
            OtherIds = IdsOf(items, KindOther),
            People = RecurringPeople(universe.People, known)
        };
    }

    /// <inheritdoc />
    public Task BuildAsync(bool force, IProgress<double> progress, CancellationToken cancellationToken)
        => Task.Run(
            () =>
            {
                var current = _snapshot ?? LoadSaved();
                if (force || current is null || _dirty || DateTime.UtcNow - current.Built > _maxAge)
                {
                    Rebuild(new Progress<double>(p => progress.Report(p * 0.9)), cancellationToken);
                }

                PrepareUsers(cancellationToken);
                progress.Report(100);
            },
            cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemUpdated -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
        _userManager.OnUserUpdated -= OnUserUpdated;
        _buildLock.Dispose();
    }

    /// <inheritdoc />
    public MetaUniverseDefinition Save(MetaUniverseDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_lock)
        {
            var stored = LoadStored();
            if (string.IsNullOrWhiteSpace(definition.Id))
            {
                definition.Id = "custom-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
                definition.Source = SourceCustom;
            }
            else
            {
                // The source is decided by the id, never by the client.
                if (_builtin.Any(b => string.Equals(b.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    definition.Source = SourceBuiltin;
                }
                else if (definition.Id.StartsWith("tmdbc-", StringComparison.OrdinalIgnoreCase))
                {
                    definition.Source = SourceAuto;
                }
                else if (stored.ContainsKey(definition.Id))
                {
                    definition.Source = SourceCustom;
                }
                else
                {
                    throw new ArgumentException("Unknown universe " + definition.Id, nameof(definition));
                }
            }

            definition.Name = string.IsNullOrWhiteSpace(definition.Name) ? definition.Id : definition.Name.Trim();
            definition.Tags = Clean(definition.Tags);
            definition.Studios = Clean(definition.Studios);
            definition.TitlePatterns = Clean(definition.TitlePatterns);
            definition.TmdbCollectionIds = Clean(definition.TmdbCollectionIds);
            definition.IncludeIds = definition.IncludeIds.Distinct().ToList();
            definition.ExcludeIds = definition.ExcludeIds.Distinct().ToList();
            definition.ChronologicalOrder = definition.ChronologicalOrder.Distinct().ToList();

            stored[definition.Id] = definition;
            Persist(stored);
        }

        Rebuild(null, CancellationToken.None);
        return definition;
    }

    /// <inheritdoc />
    public bool Delete(string id)
    {
        lock (_lock)
        {
            var stored = LoadStored();
            if (!stored.Remove(id))
            {
                return false;
            }

            Persist(stored);
        }

        Rebuild(null, CancellationToken.None);
        return true;
    }

    private static MetaUniverseDefinition Seed(string id, string name, string overview, string[] tags, string[] studios, string[] titles) => new()
    {
        Id = id,
        Name = name,
        Overview = overview,
        Source = SourceBuiltin,
        Tags = tags,
        Studios = studios,
        TitlePatterns = titles
    };

    private static List<string> Clean(IReadOnlyList<string>? values) => (values ?? Array.Empty<string>())
        .Select(v => v?.Trim() ?? string.Empty)
        .Where(v => v.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static DateTime? ReleaseDate(BaseItem item)
        => item.PremiereDate ?? (item.ProductionYear is int y && y > 0 ? new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null);

    private static DateTime When(Member member) => member.Release ?? DateTime.MaxValue;

    private static bool IsWatchable(Member member) => member.Kind is KindMovie or KindSeries;

    private static List<Guid> IdsOf(List<Member> items, string kind)
        => items.Where(i => i.Kind == kind).OrderBy(When).Select(i => i.Id).ToList();

    private static string KindOf(BaseItem item) => item switch
    {
        Movie => KindMovie,
        MediaBrowser.Controller.Entities.TV.Series => KindSeries,
        MediaBrowser.Controller.Entities.Audio.MusicAlbum => KindAlbum,
        MediaBrowser.Controller.Entities.Movies.BoxSet => KindCollection,
        _ => KindOther
    };

    private static Member ToMember(BaseItem item) => new(
        item.Id,
        KindOf(item),
        ReleaseDate(item),
        item.SortName ?? string.Empty,
        item.CommunityRating,
        item.ProductionYear,
        item.HasImage(ImageType.Backdrop),
        item.HasImage(ImageType.Primary));

    /// <summary>Lower case, without accents or punctuation, padded with spaces for whole-word "contains".</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return " ";
        }

        var sb = new StringBuilder(text.Length + 2).Append(' ');
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (sb[^1] != ' ')
            {
                sb.Append(' ');
            }
        }

        if (sb[^1] != ' ')
        {
            sb.Append(' ');
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\b(collection|colecci[oó]n|saga|trilog[iy]a?|the complete)\b|[-–:]+\s*$|^\s*[-–:]+", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionWordsRegex();

    [GeneratedRegex(@"soundtrack|banda sonora|\bost\b|\bscore\b|motion picture|music from|m[uú]sica (original|de la)|original series|serie original|original television", RegexOptions.IgnoreCase)]
    private static partial Regex SoundtrackRegex();

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or MediaBrowser.Controller.Entities.TV.Series or MediaBrowser.Controller.Entities.Audio.MusicAlbum or MediaBrowser.Controller.Entities.Movies.BoxSet or Book or MusicVideo)
        {
            _dirty = true;
        }
    }

    /// <summary>A change of parental rating or library access applies at once.</summary>
    private void OnUserUpdated(object? sender, GenericEventArgs<User> e)
    {
        foreach (var key in _visibility.Keys.Where(k => k.User == e.Argument.Id))
        {
            _visibility.TryRemove(key, out _);
        }
    }

    /// <summary>What is prepared. Only the very first time, with nothing saved yet, it is built here.</summary>
    private Snapshot GetSnapshot()
    {
        if (_snapshot is { } ready)
        {
            return ready;
        }

        if (LoadSaved() is { } saved)
        {
            return saved;
        }

        _buildLock.Wait();
        try
        {
            if (_snapshot is null)
            {
                BuildAndKeep(null, CancellationToken.None);
            }

            return _snapshot ?? new Snapshot(SnapshotVersion, DateTime.UtcNow, new List<Universe>());
        }
        finally
        {
            _buildLock.Release();
        }
    }

    private Snapshot? LoadSaved()
    {
        _buildLock.Wait();
        try
        {
            if (_snapshot is null && File.Exists(_snapshotPath))
            {
                using var file = File.OpenRead(_snapshotPath);
                var saved = JsonSerializer.Deserialize<Snapshot>(file, JsonDefaults.Options);
                _snapshot = saved?.Version == SnapshotVersion ? saved : null;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "The prepared universes could not be read");
        }
        finally
        {
            _buildLock.Release();
        }

        return _snapshot;
    }

    private void Rebuild(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        _buildLock.Wait(cancellationToken);
        try
        {
            BuildAndKeep(progress, cancellationToken);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <summary>Whoever opens the universes meanwhile keeps seeing the previous ones.</summary>
    private void BuildAndKeep(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        _dirty = false;
        try
        {
            var built = Build(progress, cancellationToken);
            _snapshot = built;
            _visibility.Clear();
            SaveSnapshot(built);
        }
        catch (OperationCanceledException)
        {
            _dirty = true;
            throw;
        }
        catch (Exception ex)
        {
            _dirty = true;
            _logger.LogError(ex, "Error building the meta universes");
        }
    }

    private void SaveSnapshot(Snapshot snapshot)
    {
        try
        {
            var temp = _snapshotPath + ".tmp";
            using (var file = File.Create(temp))
            {
                JsonSerializer.Serialize(file, snapshot, JsonDefaults.Options);
            }

            File.Move(temp, _snapshotPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "The prepared universes could not be saved");
        }
    }

    /// <summary>Works out what each user can see, so nobody waits for it when opening the universes.</summary>
    private void PrepareUsers(CancellationToken cancellationToken)
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        foreach (var user in _userManager.GetUsers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            VisibleIds(user, snapshot);
        }
    }

    /// <summary>The ids the user can see (null = everything), until the next build or a change of the user.</summary>
    private HashSet<Guid>? VisibleIds(User? user, Snapshot snapshot)
    {
        if (user is null)
        {
            return null;
        }

        return _visibility.GetOrAdd(
            (user.Id, snapshot.Built),
            _ => new Lazy<HashSet<Guid>>(() => ComputeVisible(user, snapshot))).Value;
    }

    private HashSet<Guid> ComputeVisible(User user, Snapshot snapshot)
    {
        var result = new HashSet<Guid>();
        foreach (var chunk in snapshot.Universes.SelectMany(u => u.Members).Select(m => m.Id).Distinct().Chunk(BatchSize))
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = chunk });
            result.UnionWith(items.Where(i => i.IsVisibleStandalone(user)).Select(i => i.Id));
        }

        return result;
    }

    private Snapshot Build(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        Dictionary<string, MetaUniverseDefinition> stored;
        lock (_lock)
        {
            stored = new Dictionary<string, MetaUniverseDefinition>(LoadStored(), StringComparer.OrdinalIgnoreCase);
        }

        var all = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = _kinds,
            Recursive = true,
            IsVirtualItem = false
        });

        var media = all.Where(i => i is not MediaBrowser.Controller.Entities.Audio.MusicAlbum && i is not MediaBrowser.Controller.Entities.Movies.BoxSet).ToList();
        var albums = all.OfType<MediaBrowser.Controller.Entities.Audio.MusicAlbum>().ToList();
        var boxSets = all.OfType<MediaBrowser.Controller.Entities.Movies.BoxSet>().ToList();

        // Built-in universes (with the administrator's changes)
        var definitions = _builtin
            .Select(b => stored.TryGetValue(b.Id, out var o) ? o : b)
            .ToList();

        // Movies already covered by a built-in universe
        var covered = new HashSet<Guid>();
        foreach (var def in definitions)
        {
            foreach (var item in media.Where(m => m is Movie && Matches(def, m)))
            {
                covered.Add(item.Id);
            }
        }

        // Automatic universes: TMDb collections with several movies in the libraries
        foreach (var group in media.OfType<Movie>()
            .Select(m => (Movie: m, Collection: m.GetProviderId(MetadataProvider.TmdbCollection)))
            .Where(x => !string.IsNullOrEmpty(x.Collection))
            .GroupBy(x => x.Collection!, StringComparer.Ordinal))
        {
            var movies = group.Select(x => x.Movie).ToList();
            if (movies.Count < MinAutoMovies || movies.Count(m => covered.Contains(m.Id)) * 10 >= movies.Count * 6)
            {
                continue;
            }

            var id = "tmdbc-" + group.Key;
            if (stored.TryGetValue(id, out var overridden))
            {
                definitions.Add(overridden);
                continue;
            }

            var boxSet = boxSets.FirstOrDefault(b => string.Equals(b.GetProviderId(MetadataProvider.TmdbCollection), group.Key, StringComparison.Ordinal));
            var name = CollectionWordsRegex().Replace(boxSet?.Name ?? movies.OrderBy(ReleaseDate).First().Name, string.Empty).Trim(' ', '-', ':', '–');
            var titles = name.Length >= 4 ? new[] { "^" + Regex.Escape(name) + @"(\b|$)" } : Array.Empty<string>();
            definitions.Add(new MetaUniverseDefinition
            {
                Id = id,
                Name = name,
                Overview = boxSet?.Overview,
                Source = SourceAuto,
                TmdbCollectionIds = new[] { group.Key },
                TitlePatterns = titles,
                BackdropItemId = boxSet?.Id
            });
        }

        // Custom universes
        definitions.AddRange(stored.Values.Where(s => s.Source == SourceCustom));

        var byId = all.ToDictionary(i => i.Id);
        var boxSetChildren = boxSets.ToDictionary(b => b.Id, b => b.GetLinkedChildren().Select(c => c.Id).ToList());
        var found = new List<(MetaUniverseDefinition Definition, List<Member> Members)>(definitions.Count);
        for (var index = 0; index < definitions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(index * 85.0 / definitions.Count);
            var def = definitions[index];
            var exclude = def.ExcludeIds.ToHashSet();
            var members = new HashSet<Guid>(media.Where(m => !exclude.Contains(m.Id) && Matches(def, m)).Select(m => m.Id));
            foreach (var id in def.IncludeIds)
            {
                if (!exclude.Contains(id) && byId.ContainsKey(id))
                {
                    members.Add(id);
                }
            }

            // Soundtracks: albums named after the universe or after one of its titles
            var titles = members.Select(i => byId[i])
                .Where(i => i is Movie || i is MediaBrowser.Controller.Entities.TV.Series)
                .SelectMany(i => new[] { Normalize(i.Name), Normalize(i.OriginalTitle) })
                .Where(t => t.Length >= 7)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var album in albums)
            {
                if (exclude.Contains(album.Id) || members.Contains(album.Id))
                {
                    continue;
                }

                if (MatchesTitle(def, album) || (IsSoundtrack(album) && ContainsAny(Normalize(album.Name), titles)))
                {
                    members.Add(album.Id);
                }
            }

            // Collections whose movies belong to the universe
            foreach (var boxSet in boxSets)
            {
                if (exclude.Contains(boxSet.Id) || members.Contains(boxSet.Id))
                {
                    continue;
                }

                var collectionId = boxSet.GetProviderId(MetadataProvider.TmdbCollection);
                var inUniverse = (collectionId is not null && def.TmdbCollectionIds.Contains(collectionId, StringComparer.Ordinal)) || MatchesTitle(def, boxSet);
                if (!inUniverse)
                {
                    var children = boxSetChildren[boxSet.Id];
                    var hits = children.Count(members.Contains);
                    inUniverse = hits >= 2 && hits * 2 >= children.Count;
                }

                if (inUniverse)
                {
                    members.Add(boxSet.Id);
                }
            }

            if (members.Count > 0 || def.Source == SourceCustom)
            {
                found.Add((def, members.Select(i => ToMember(byId[i])).ToList()));
            }
        }

        var credits = LoadCredits(found.SelectMany(f => f.Members).Where(IsWatchable).Select(m => m.Id).Distinct(), cancellationToken);
        var universes = found.Select(f => new Universe(f.Definition, f.Members, BuildPeople(f.Members, credits))).ToList();
        progress?.Report(100);
        _logger.LogInformation("Meta universes built: {Count} from {Items} items in {Ms} ms", universes.Count, all.Count, (int)(DateTime.UtcNow - started).TotalMilliseconds);
        return new Snapshot(SnapshotVersion, DateTime.UtcNow, universes);
    }

    private Dictionary<Guid, IReadOnlyList<PersonInfo>> LoadCredits(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyList<PersonInfo>>();
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (itemId, people) in _libraryManager.GetPeopleByItems(chunk))
            {
                result[itemId] = people;
            }
        }

        return result;
    }

    private static bool ContainsAny(string text, List<string> needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSoundtrack(BaseItem album)
        => SoundtrackRegex().IsMatch(album.Name ?? string.Empty)
           || album.Genres.Any(g => _soundtrackGenres.Contains(g.Trim().ToLowerInvariant()));

    private bool Matches(MetaUniverseDefinition def, BaseItem item)
    {
        if (def.Tags.Count > 0 && item.Tags.Any(t => def.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (def.Studios.Count > 0 && item.Studios.Any(s => def.Studios.Contains(s, StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (def.TmdbCollectionIds.Count > 0 && item is Movie)
        {
            var collection = item.GetProviderId(MetadataProvider.TmdbCollection);
            if (collection is not null && def.TmdbCollectionIds.Contains(collection, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return MatchesTitle(def, item);
    }

    private bool MatchesTitle(MetaUniverseDefinition def, BaseItem item)
    {
        foreach (var pattern in def.TitlePatterns)
        {
            var regex = _regexCache.GetOrAdd(pattern, p =>
            {
                try
                {
                    return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                }
                catch (ArgumentException)
                {
                    return null;
                }
            });
            if (regex is null)
            {
                continue;
            }

            try
            {
                if (regex.IsMatch(item.Name ?? string.Empty) || (!string.IsNullOrEmpty(item.OriginalTitle) && regex.IsMatch(item.OriginalTitle)))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // a bad pattern must not break the build
            }
        }

        return false;
    }

    private static List<Member> Visible(IReadOnlyList<Member> members, HashSet<Guid>? visible)
        => visible is null ? members.ToList() : members.Where(m => visible.Contains(m.Id)).ToList();

    private static MetaUniverseSummary Summarize(MetaUniverseDefinition def, List<Member> items)
    {
        var watchable = items.Where(IsWatchable).ToList();
        var years = watchable.Where(i => i.Year > 0).Select(i => i.Year!.Value).ToList();
        var byPopularity = watchable.OrderByDescending(i => i.Rating ?? 0).ToList();

        var backdrops = new List<Guid>();
        if (def.BackdropItemId is Guid preferred && items.Any(i => i.Id == preferred && i.HasBackdrop))
        {
            backdrops.Add(preferred);
        }

        backdrops.AddRange(byPopularity.Where(i => i.HasBackdrop && i.Id != def.BackdropItemId).Take(6).Select(i => i.Id));

        return new MetaUniverseSummary
        {
            Id = def.Id,
            Name = def.Name,
            Overview = def.Overview,
            Source = def.Source,
            Hidden = def.Hidden,
            MovieCount = items.Count(i => i.Kind == KindMovie),
            SeriesCount = items.Count(i => i.Kind == KindSeries),
            AlbumCount = items.Count(i => i.Kind == KindAlbum),
            CollectionCount = items.Count(i => i.Kind == KindCollection),
            FirstYear = years.Count > 0 ? years.Min() : null,
            LastYear = years.Count > 0 ? years.Max() : null,
            BackdropItemIds = backdrops,
            PosterItemIds = byPopularity.Where(i => i.HasPrimary).Take(8).Select(i => i.Id).ToList()
        };
    }

    /// <summary>The actors of a universe, with the title of each role, to count only what each user can see.</summary>
    private List<Person> BuildPeople(List<Member> members, Dictionary<Guid, IReadOnlyList<PersonInfo>> credits)
    {
        var byName = new Dictionary<string, (Guid Id, List<Credit> Credits)>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members.Where(IsWatchable))
        {
            if (!credits.TryGetValue(member.Id, out var list))
            {
                continue;
            }

            foreach (var person in list.Where(p => p.Type is PersonKind.Actor or PersonKind.GuestStar && !string.IsNullOrWhiteSpace(p.Name)))
            {
                if (!byName.TryGetValue(person.Name, out var entry))
                {
                    entry = (person.Id, new List<Credit>());
                    byName[person.Name] = entry;
                }

                entry.Credits.Add(new Credit(member.Id, person.Role?.Trim()));
            }
        }

        return byName
            .Where(p => p.Value.Credits.Select(c => c.ItemId).Distinct().Skip(1).Any())
            .OrderByDescending(p => p.Value.Credits.Select(c => c.ItemId).Distinct().Count())
            .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxStoredPeople)
            .Select(p => new Person(p.Value.Id != Guid.Empty ? p.Value.Id : (_libraryManager.GetPerson(p.Key)?.Id ?? Guid.Empty), p.Key, p.Value.Credits))
            .ToList();
    }

    private static List<MetaUniversePerson> RecurringPeople(IReadOnlyList<Person> people, HashSet<Guid> visible)
    {
        var result = new List<MetaUniversePerson>();
        foreach (var person in people)
        {
            var credits = person.Credits.Where(c => visible.Contains(c.ItemId)).ToList();
            var appearances = credits.Select(c => c.ItemId).Distinct().Count();
            if (appearances < 2)
            {
                continue;
            }

            result.Add(new MetaUniversePerson
            {
                Id = person.Id,
                Name = person.Name,
                Appearances = appearances,
                Roles = credits
                    .Where(c => !string.IsNullOrWhiteSpace(c.Role))
                    .GroupBy(c => c.Role!, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .Take(3)
                    .ToList()
            });
        }

        return result
            .OrderByDescending(p => p.Appearances)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(30)
            .ToList();
    }

    private Dictionary<string, MetaUniverseDefinition> LoadStored()
    {
        if (_stored is not null)
        {
            return _stored;
        }

        var result = new Dictionary<string, MetaUniverseDefinition>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_storePath))
            {
                var list = JsonSerializer.Deserialize<List<MetaUniverseDefinition>>(File.ReadAllText(_storePath), JsonDefaults.Options);
                foreach (var def in list ?? new List<MetaUniverseDefinition>())
                {
                    if (!string.IsNullOrWhiteSpace(def.Id))
                    {
                        result[def.Id] = def;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read {Path}", _storePath);
        }

        _stored = result;
        return result;
    }

    private void Persist(Dictionary<string, MetaUniverseDefinition> stored)
    {
        var temp = _storePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored.Values.ToList(), JsonDefaults.Options));
        File.Move(temp, _storePath, true);
    }

    private sealed record Member(Guid Id, string Kind, DateTime? Release, string SortName, float? Rating, int? Year, bool HasBackdrop, bool HasPrimary);

    private sealed record Credit(Guid ItemId, string? Role);

    private sealed record Person(Guid Id, string Name, List<Credit> Credits);

    private sealed record Universe(MetaUniverseDefinition Definition, List<Member> Members, List<Person> People);

    private sealed record Snapshot(int Version, DateTime Built, List<Universe> Universes);
}
