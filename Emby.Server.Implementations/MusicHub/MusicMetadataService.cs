using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MusicHub;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.MusicHub;

/// <summary>
/// Completes albums and artists with Deezer, iTunes, Wikipedia and Last.fm. It only fills what is empty, and only when
/// artist and album both match: when in doubt it leaves the item as it was. Images go to the data folder of the
/// server, so the music drive is not touched.
/// </summary>
public sealed partial class MusicMetadataService : IMusicMetadataService
{
    private const string DeezerUrl = "https://api.deezer.com/";
    private const string ItunesUrl = "https://itunes.apple.com/search?entity=album&limit=25&country=";
    private const string VariousArtists = "variousartists";
    private const int DeezerQuotaError = 4;
    private const int MaxCandidates = 3;
    private const int MaxAlbumsPerArtist = 3;
    private const int MaxGenres = 3;
    private const int SaveEvery = 25;

    private static readonly TimeSpan _scanCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _retryMissingAfter = TimeSpan.FromDays(30);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<MusicMetadataService> _logger;
    private readonly MusicCatalog _deezer;
    private readonly MusicCatalog _itunes;
    private readonly MusicCatalog _wikipedia;
    private readonly MusicCatalog _lastFm;
    private readonly MusicTextFinder _text;
    private readonly HashSet<string> _countriesWithoutItunes = new(StringComparer.Ordinal);
    private readonly object _settingsLock = new();
    private MetadataSettings? _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicMetadataService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="paths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public MusicMetadataService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths paths,
        ILogger<MusicMetadataService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _paths = paths;
        _logger = logger;

        // Deezer does not publish its limit, iTunes is known to allow about 20 requests a minute, Wikimedia allows 10
        // a minute to clients that do not identify themselves with a contact, and Last.fm suspends accounts that make
        // several calls a second
        _deezer = new MusicCatalog("Deezer", TimeSpan.FromMilliseconds(500), httpClientFactory);
        _itunes = new MusicCatalog("iTunes", TimeSpan.FromMilliseconds(3200), httpClientFactory);
        _wikipedia = new MusicCatalog("Wikipedia", TimeSpan.FromMilliseconds(6500), httpClientFactory);
        _lastFm = new MusicCatalog("Last.fm", TimeSpan.FromMilliseconds(1000), httpClientFactory);
        _text = new MusicTextFinder(_wikipedia, _lastFm, logger);
    }

    /// <inheritdoc />
    public bool HasLastFmKey => !string.IsNullOrEmpty(Settings().LastFmKey);

    private string TriedPath => Path.Combine(_paths.DataPath, "music-metadata.json");

    private string TriedTextPath => Path.Combine(_paths.DataPath, "music-metadata-text.json");

    private string SettingsPath => Path.Combine(_paths.DataPath, "music-metadata-settings.json");

    /// <inheritdoc />
    public async Task<bool> SetLastFmKeyAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var clean = key.Trim();
        if (!await _text.IsValidKeyAsync(clean, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // What was already tried without Last.fm gets another chance with it
        SaveSettings(new MetadataSettings(clean, DateTime.UtcNow));
        return true;
    }

    /// <inheritdoc />
    public void RemoveLastFmKey() => SaveSettings(new MetadataSettings(null, null));

    /// <inheritdoc />
    public async Task FillAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        await WaitForScanAsync(cancellationToken).ConfigureAwait(false);
        _deezer.Failures = 0;
        _itunes.Failures = 0;
        _text.Reset();

        var tried = LoadTried(TriedPath);
        var triedText = LoadTried(TriedTextPath);
        var keyAddedAt = Settings().LastFmAddedAt;
        var albums = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Recursive = true,
            IsVirtualItem = false
        }).OfType<MusicAlbum>().Where(a => !a.IsLocked).ToList();

        var titlesByArtist = albums
            .Where(a => ArtistOf(a) is not null)
            .GroupBy(a => MusicNames.Comparable(ArtistOf(a)))
            .ToDictionary(g => g.Key, g => g.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Take(MaxAlbumsPerArtist).ToList());

        var artists = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.MusicArtist],
            Recursive = true
        }).OfType<MusicArtist>()
            .Where(a => !a.IsLocked && titlesByArtist.ContainsKey(MusicNames.Comparable(a.Name)))
            .ToList();

        var dataAlbums = albums
            .Where(a => !IsSettled(tried, a.Id, null) && (!a.HasImage(ImageType.Primary, 0) || !a.ProductionYear.HasValue || WantsGenres(a)))
            .Select(a => a.Id)
            .ToList();
        var dataArtists = artists
            .Where(a => !IsSettled(tried, a.Id, null) && !a.HasImage(ImageType.Primary, 0))
            .Select(a => a.Id)
            .ToList();
        var textAlbums = albums
            .Where(a => ArtistOf(a) is not null && !IsSettled(triedText, a.Id, keyAddedAt) && WantsWords(a))
            .Select(a => a.Id)
            .ToList();
        var textArtists = artists
            .Where(a => !IsSettled(triedText, a.Id, keyAddedAt) && WantsWords(a))
            .Select(a => a.Id)
            .ToList();

        var total = dataAlbums.Count + dataArtists.Count + textAlbums.Count + textArtists.Count;
        int done = 0, completed = 0;

        async Task RunAsync(List<Guid> ids, Dictionary<Guid, DateTime> memory, Func<bool> isOff, Func<BaseItem, Task<(bool Changed, bool Settled)>> fill)
        {
            foreach (var id in ids)
            {
                if (isOff())
                {
                    return;
                }

                await WaitForScanAsync(cancellationToken).ConfigureAwait(false);
                if (_libraryManager.GetItemById(id) is { IsLocked: false } item)
                {
                    var (changed, settled) = await fill(item).ConfigureAwait(false);
                    completed += changed ? 1 : 0;
                    Remember(memory, id, settled);
                }

                done++;
                if (done % SaveEvery == 0)
                {
                    SaveTried(TriedPath, tried);
                    SaveTried(TriedTextPath, triedText);
                }

                progress.Report(100.0 * done / Math.Max(1, total));
            }
        }

        bool TextIsOff() => _wikipedia.IsOff && (_lastFm.IsOff || !HasLastFmKey);
        IReadOnlyList<string> TitlesOf(BaseItem artist)
            => titlesByArtist.TryGetValue(MusicNames.Comparable(artist.Name), out var titles) ? titles : [];

        try
        {
            // Covers, years and photos go first: the texts take much longer because of the limits of Wikipedia
            await RunAsync(dataAlbums, tried, () => _deezer.IsOff && _itunes.IsOff, i => FillAlbumDataAsync((MusicAlbum)i, cancellationToken)).ConfigureAwait(false);
            await RunAsync(dataArtists, tried, () => _deezer.IsOff, i => FillArtistPhotoAsync((MusicArtist)i, TitlesOf(i), cancellationToken)).ConfigureAwait(false);
            await RunAsync(textAlbums, triedText, TextIsOff, i => FillAlbumTextAsync((MusicAlbum)i, cancellationToken)).ConfigureAwait(false);
            await RunAsync(textArtists, triedText, TextIsOff, i => FillArtistTextAsync((MusicArtist)i, TitlesOf(i), cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            SaveTried(TriedPath, tried);
            SaveTried(TriedTextPath, triedText);
        }

        if (_deezer.IsOff || _itunes.IsOff || _wikipedia.IsOff || _lastFm.IsOff)
        {
            _logger.LogWarning("A music catalog stopped answering; the task will continue on its next run");
        }

        progress.Report(100);
        _logger.LogInformation("Music metadata: {Items} albums and artists completed", completed);
    }

    private static bool IsSettled(Dictionary<Guid, DateTime> tried, Guid id, DateTime? keyAddedAt)
        => tried.TryGetValue(id, out var at)
            && DateTime.UtcNow - at < _retryMissingAfter
            && (!keyAddedAt.HasValue || at >= keyAddedAt.Value);

    private static void Remember(Dictionary<Guid, DateTime> tried, Guid id, bool settled)
    {
        if (settled)
        {
            tried[id] = DateTime.UtcNow;
        }
    }

    private static bool WantsGenres(BaseItem item)
        => item.Genres.Length == 0 && !item.LockedFields.Contains(MetadataField.Genres);

    private static bool WantsText(BaseItem item)
        => string.IsNullOrWhiteSpace(item.Overview) && !item.LockedFields.Contains(MetadataField.Overview);

    private bool WantsWords(BaseItem item) => WantsText(item) || (HasLastFmKey && WantsGenres(item));

    private static string? ArtistOf(MusicAlbum album)
    {
        var artist = album.AlbumArtist ?? album.Artists.FirstOrDefault();
        return string.IsNullOrWhiteSpace(artist) || MusicNames.Comparable(artist) == VariousArtists ? null : artist;
    }

    /// <summary>Deezer answers with an address without image when it has no picture.</summary>
    private static string? RealPicture(string? url)
        => string.IsNullOrWhiteSpace(url) || url.Contains("/cover//", StringComparison.Ordinal) || url.Contains("/artist//", StringComparison.Ordinal)
            ? null
            : url;

    private static bool TakeYearFromFolder(MusicAlbum album)
    {
        if (album.ProductionYear.HasValue || string.IsNullOrEmpty(album.Path))
        {
            return false;
        }

        var match = FolderYearRegex().Match(Path.GetFileName(album.Path.TrimEnd('/', '\\')));
        if (!match.Success
            || !int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !MusicNames.IsValidYear(year))
        {
            return false;
        }

        album.ProductionYear = year;
        return true;
    }

    private static bool ApplyData(MusicAlbum album, Found found)
    {
        var changed = false;
        if (!album.ProductionYear.HasValue && found.Date.HasValue)
        {
            album.ProductionYear = found.Date.Value.Year;
            changed = true;
        }

        if (!album.PremiereDate.HasValue && found.Date?.Year == album.ProductionYear)
        {
            album.PremiereDate = found.Date;
            changed = true;
        }

        if (WantsGenres(album) && found.Genres.Length > 0)
        {
            album.Genres = found.Genres;
            changed = true;
        }

        return changed;
    }

    /// <summary>Puts what was found in the empty fields.</summary>
    private static bool ApplyText(BaseItem item, MusicTextResult found)
    {
        var changed = false;
        if (WantsText(item) && found.Text is not null)
        {
            item.Overview = found.Text;
            changed = true;
        }

        if (WantsGenres(item) && found.Tags.Length > 0)
        {
            item.Genres = found.Tags;
            changed = true;
        }

        return changed;
    }

    /// <summary>Every library can have its own language and country; without one it uses those of the server.</summary>
    private MetadataLocale UseLocaleOf(BaseItem item)
    {
        var locale = MetadataLocale.From(item.GetPreferredMetadataLanguage(), item.GetPreferredMetadataCountryCode());
        _deezer.Language = locale.Language;
        _itunes.Language = locale.Language;
        _wikipedia.Language = locale.Language;
        _lastFm.Language = locale.Language;
        return locale;
    }

    private void ThrowOnQuota(DeezerError? error)
    {
        if (error?.Code == DeezerQuotaError)
        {
            _deezer.BackOff();
            throw new HttpRequestException("Deezer asked to wait", null, HttpStatusCode.TooManyRequests);
        }
    }

    /// <summary>The library writes to the same items while it scans, so this waits for it to finish.</summary>
    private async Task WaitForScanAsync(CancellationToken cancellationToken)
    {
        while (_libraryManager.IsScanRunning)
        {
            await Task.Delay(_scanCheck, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(bool Changed, bool Settled)> FillAlbumDataAsync(MusicAlbum album, CancellationToken cancellationToken)
    {
        var locale = UseLocaleOf(album);
        var changed = TakeYearFromFolder(album);
        var hasCover = album.HasImage(ImageType.Primary, 0) || album.Tracks.Any(t => t.HasImage(ImageType.Primary, 0));
        var artist = ArtistOf(album);
        var settled = true;

        if (artist is not null && !string.IsNullOrWhiteSpace(album.Name))
        {
            foreach (var catalog in new[] { _deezer, _itunes })
            {
                if (hasCover && album.ProductionYear.HasValue && !WantsGenres(album))
                {
                    break;
                }

                if (catalog.IsOff)
                {
                    settled = false;
                    continue;
                }

                try
                {
                    var found = catalog == _deezer
                        ? await FromDeezerAsync(artist, album.Name, album.ProductionYear, cancellationToken).ConfigureAwait(false)
                        : await FromItunesAsync(artist, album.Name, album.ProductionYear, locale.Country, cancellationToken).ConfigureAwait(false);
                    catalog.Failures = 0;
                    if (found is not null)
                    {
                        changed |= ApplyData(album, found);
                        if (!hasCover && found.Cover is not null)
                        {
                            await _providerManager.SaveImage(album, found.Cover, ImageType.Primary, null, cancellationToken).ConfigureAwait(false);
                            hasCover = true;
                            changed = true;
                        }
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogDebug(ex, "{Catalog} did not answer for album {Id}", catalog.Name, album.Id);
                    catalog.Failures++;
                    settled = false;
                }
            }
        }

        if (changed)
        {
            await album.UpdateToRepositoryAsync(ItemUpdateType.MetadataDownload | ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        }

        return (changed, settled);
    }

    private async Task<(bool Changed, bool Settled)> FillAlbumTextAsync(MusicAlbum album, CancellationToken cancellationToken)
    {
        var artist = ArtistOf(album);
        if (artist is null || string.IsNullOrWhiteSpace(album.Name) || !WantsWords(album))
        {
            return (false, true);
        }

        var text = await _text.FindAlbumAsync(
            artist,
            album.Name,
            album.ProductionYear,
            album.GetProviderId(MetadataProvider.MusicBrainzReleaseGroup),
            UseLocaleOf(album),
            WantsText(album),
            WantsGenres(album),
            Settings().LastFmKey,
            cancellationToken).ConfigureAwait(false);
        var changed = ApplyText(album, text);
        if (changed)
        {
            await album.UpdateToRepositoryAsync(ItemUpdateType.MetadataDownload, cancellationToken).ConfigureAwait(false);
        }

        return (changed, text.Complete);
    }

    private async Task<(bool Changed, bool Settled)> FillArtistTextAsync(MusicArtist artist, IReadOnlyList<string> albumTitles, CancellationToken cancellationToken)
    {
        if (!WantsWords(artist))
        {
            return (false, true);
        }

        var text = await _text.FindArtistAsync(
            artist.Name,
            artist.GetProviderId(MetadataProvider.MusicBrainzArtist),
            albumTitles,
            UseLocaleOf(artist),
            WantsText(artist),
            WantsGenres(artist),
            Settings().LastFmKey,
            cancellationToken).ConfigureAwait(false);
        var changed = ApplyText(artist, text);
        if (changed)
        {
            await artist.UpdateToRepositoryAsync(ItemUpdateType.MetadataDownload, cancellationToken).ConfigureAwait(false);
        }

        return (changed, text.Complete);
    }

    private async Task<(bool Changed, bool Settled)> FillArtistPhotoAsync(MusicArtist artist, IReadOnlyList<string> albumTitles, CancellationToken cancellationToken)
    {
        if (artist.HasImage(ImageType.Primary, 0))
        {
            return (false, true);
        }

        // An artist is only trusted through one of their albums: many artists share a name
        foreach (var title in albumTitles)
        {
            try
            {
                var rows = await SearchDeezerAsync(artist.Name, title, cancellationToken).ConfigureAwait(false);
                _deezer.Failures = 0;
                var picture = rows.Select(r => RealPicture(r.Artist?.PictureXl)).FirstOrDefault(p => p is not null);
                if (picture is not null)
                {
                    await _providerManager.SaveImage(artist, picture, ImageType.Primary, null, cancellationToken).ConfigureAwait(false);
                    await artist.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
                    return (true, true);
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "Deezer did not answer for artist {Id}", artist.Id);
                _deezer.Failures++;
                return (false, false);
            }
        }

        return (false, true);
    }

    private async Task<List<DeezerRow>> SearchDeezerAsync(string artist, string title, CancellationToken cancellationToken)
    {
        var url = DeezerUrl + "search/album?limit=25&q=" + Uri.EscapeDataString(artist + " " + MusicNames.CleanTitle(title));
        var answer = await _deezer.GetJsonAsync<DeezerSearch>(url, cancellationToken).ConfigureAwait(false);
        ThrowOnQuota(answer?.Error);

        var wantedArtist = MusicNames.Comparable(artist);
        return (answer?.Data ?? [])
            .Where(r => MusicNames.Comparable(r.Artist?.Name) == wantedArtist && MusicNames.TitleScore(title, r.Title) > 0)
            .OrderByDescending(r => MusicNames.TitleScore(title, r.Title))
            .ToList();
    }

    private async Task<Found?> FromDeezerAsync(string artist, string title, int? year, CancellationToken cancellationToken)
    {
        var rows = await SearchDeezerAsync(artist, title, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows.Take(MaxCandidates))
        {
            var url = DeezerUrl + "album/" + row.Id.ToString(CultureInfo.InvariantCulture);
            var album = await _deezer.GetJsonAsync<DeezerAlbum>(url, cancellationToken).ConfigureAwait(false);
            ThrowOnQuota(album?.Error);
            if (album is null || album.Error is not null)
            {
                continue;
            }

            var date = MusicNames.ParseDate(album.ReleaseDate);
            if (MusicNames.FitsYear(year, date?.Year))
            {
                var genres = (album.Genres?.Data ?? [])
                    .Select(g => g.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxGenres)
                    .ToArray();
                return new Found(RealPicture(album.CoverXl ?? row.CoverXl), date, genres);
            }
        }

        return null;
    }

    private async Task<Found?> FromItunesAsync(string artist, string title, int? year, string country, CancellationToken cancellationToken)
    {
        var terms = Uri.EscapeDataString(artist + " " + MusicNames.CleanTitle(title));
        var store = _countriesWithoutItunes.Contains(country) ? MetadataLocale.FallbackCountry : country;
        var answer = await _itunes.GetJsonAsync<ItunesSearch>(ItunesUrl + store + "&term=" + terms, cancellationToken).ConfigureAwait(false);
        if (answer is null && store != MetadataLocale.FallbackCountry)
        {
            // Not every country has an iTunes store
            _countriesWithoutItunes.Add(store);
            answer = await _itunes.GetJsonAsync<ItunesSearch>(ItunesUrl + MetadataLocale.FallbackCountry + "&term=" + terms, cancellationToken).ConfigureAwait(false);
        }

        var wantedArtist = MusicNames.Comparable(artist);
        var match = (answer?.Results ?? [])
            .Where(r => MusicNames.Comparable(r.ArtistName) == wantedArtist && MusicNames.TitleScore(title, r.CollectionName) > 0)
            .Where(r => MusicNames.FitsYear(year, MusicNames.ParseDate(r.ReleaseDate)?.Year))
            .OrderByDescending(r => MusicNames.TitleScore(title, r.CollectionName))
            .FirstOrDefault();
        if (match is null)
        {
            return null;
        }

        var genre = match.PrimaryGenreName;
        var genres = string.IsNullOrWhiteSpace(genre) || MusicNames.Comparable(genre) is "music" or "musica" or "musique" or "musik" ? [] : new[] { genre };
        var cover = match.ArtworkUrl100?.Replace("100x100bb", "1000x1000bb", StringComparison.Ordinal);
        return new Found(RealPicture(cover), MusicNames.ParseDate(match.ReleaseDate), genres);
    }

    private MetadataSettings Settings()
    {
        lock (_settingsLock)
        {
            if (_settings is not null)
            {
                return _settings;
            }

            try
            {
                if (File.Exists(SettingsPath))
                {
                    using var stream = File.OpenRead(SettingsPath);
                    _settings = JsonSerializer.Deserialize<MetadataSettings>(stream, JsonDefaults.Options);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "The music metadata settings could not be read");
            }

            _settings ??= new MetadataSettings(null, null);
            return _settings;
        }
    }

    private void SaveSettings(MetadataSettings settings)
    {
        lock (_settingsLock)
        {
            var temporary = SettingsPath + ".tmp";
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, settings, JsonDefaults.Options);
            }

            File.Move(temporary, SettingsPath, true);
            _settings = settings;
        }
    }

    private Dictionary<Guid, DateTime> LoadTried(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize<Dictionary<Guid, DateTime>>(stream, JsonDefaults.Options) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "The progress of the music metadata could not be read; it will be prepared again");
        }

        return [];
    }

    private void SaveTried(string path, Dictionary<Guid, DateTime> tried)
    {
        try
        {
            var temporary = path + ".tmp";
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, tried, JsonDefaults.Options);
            }

            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The progress of the music metadata could not be saved");
        }
    }

    [GeneratedRegex(@"[\(\[]((?:19|20)\d{2})[\)\]]\s*$")]
    private static partial Regex FolderYearRegex();

    private sealed record MetadataSettings(string? LastFmKey, DateTime? LastFmAddedAt);

    private sealed record Found(string? Cover, DateTime? Date, string[] Genres);

    private sealed record DeezerError(int Code);

    private sealed record DeezerSearch(List<DeezerRow>? Data, DeezerError? Error);

    private sealed record DeezerArtist(string? Name, [property: JsonPropertyName("picture_xl")] string? PictureXl);

    private sealed record DeezerRow(long Id, string? Title, [property: JsonPropertyName("cover_xl")] string? CoverXl, DeezerArtist? Artist);

    private sealed record DeezerGenre(string? Name);

    private sealed record DeezerGenres(List<DeezerGenre>? Data);

    private sealed record DeezerAlbum(
        [property: JsonPropertyName("cover_xl")] string? CoverXl,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        DeezerGenres? Genres,
        DeezerError? Error);

    private sealed record ItunesSearch(List<ItunesAlbum>? Results);

    private sealed record ItunesAlbum(string? CollectionName, string? ArtistName, string? ArtworkUrl100, string? ReleaseDate, string? PrimaryGenreName);
}
