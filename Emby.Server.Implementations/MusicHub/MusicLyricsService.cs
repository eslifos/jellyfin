using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.MusicHub;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Lyrics;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.MusicHub;

/// <summary>
/// Finds lyrics on LRCLIB and keeps them in the data folder of the server. They are not written next to the songs and
/// the songs are not scanned again: the music lives on a remote drive and that would use up its quota.
/// </summary>
public sealed partial class MusicLyricsService : IMusicLyricsService, IDisposable
{
    private const string BaseUrl = "https://lrclib.net/api/";
    private const string ClientName = "CosmicMusic/1.0";
    private const int MaxOptions = 12;
    private const int MaxFailuresInARow = 5;
    private const int SaveEvery = 25;
    private const int MatchSeconds = 3;

    // LRCLIB allows about 90 requests a minute
    private static readonly TimeSpan _spacing = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan _playerTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _defaultBackOff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _retryMissingAfter = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions _lrclibJson = new() { PropertyNameCaseInsensitive = true };

    private readonly ILibraryManager _libraryManager;
    private readonly ILyricManager _lyricManager;
    private readonly ILyricParser[] _parsers;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<MusicLyricsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _missLock = new();
    private ConcurrentDictionary<Guid, Miss>? _misses;
    private DateTime _nextRequest = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicLyricsService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="lyricManager">The lyric manager.</param>
    /// <param name="parsers">The lyric parsers.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="paths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public MusicLyricsService(
        ILibraryManager libraryManager,
        ILyricManager lyricManager,
        IEnumerable<ILyricParser> parsers,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths paths,
        ILogger<MusicLyricsService> logger)
    {
        _libraryManager = libraryManager;
        _lyricManager = lyricManager;
        _parsers = parsers.OrderBy(p => p.Priority).ToArray();
        _httpClientFactory = httpClientFactory;
        _paths = paths;
        _logger = logger;
    }

    private string StoreFolder => Path.Combine(_paths.DataPath, "music-lyrics");

    private string MissPath => Path.Combine(StoreFolder, "missing.json");

    /// <inheritdoc />
    public async Task<MusicLyrics> GetAsync(Audio song, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(song);

        var kept = await ReadKnownAsync(song, cancellationToken).ConfigureAwait(false);
        if (kept is not null)
        {
            return kept;
        }

        if (Misses().TryGetValue(song.Id, out var miss) && IsSettled(miss))
        {
            return Empty(miss.Instrumental);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_playerTimeout);
        try
        {
            var found = await LookUpAsync(song, timeout.Token).ConfigureAwait(false);
            SaveMisses();
            return found;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "LRCLIB did not answer");
            return Empty(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Empty(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MusicLyricOption>> SearchAsync(Audio song, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(song);

        var records = await SearchRecordsAsync(song, cancellationToken).ConfigureAwait(false);
        var seconds = Seconds(song);
        return records
            .Where(r => !r.Instrumental && HasText(r))
            .OrderBy(r => seconds.HasValue && r.Duration.HasValue ? Math.Abs(r.Duration.Value - seconds.Value) > MatchSeconds : false)
            .ThenByDescending(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics))
            .ThenBy(r => seconds.HasValue && r.Duration.HasValue ? Math.Abs(r.Duration.Value - seconds.Value) : double.MaxValue)
            .Take(MaxOptions)
            .Select(r => new MusicLyricOption(
                r.Id,
                r.TrackName ?? string.Empty,
                r.ArtistName ?? string.Empty,
                r.AlbumName,
                r.Duration.HasValue ? (int)Math.Round(r.Duration.Value) : null,
                !string.IsNullOrWhiteSpace(r.SyncedLyrics),
                FirstLine(r)))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<MusicLyrics?> ChooseAsync(Audio song, long optionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(song);

        var record = await RequestAsync<LrclibRecord>("get/" + optionId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        if (record is null || !HasText(record))
        {
            return null;
        }

        var lyrics = await KeepAsync(song.Id, record, true, cancellationToken).ConfigureAwait(false);
        if (lyrics is not null && Misses().TryRemove(song.Id, out _))
        {
            SaveMisses();
        }

        return lyrics;
    }

    /// <inheritdoc />
    public async Task FillAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var songs = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true,
            IsVirtualItem = false
        }).OfType<Audio>().Where(a => a.GetType() == typeof(Audio) && a.ExtraType is null).ToList();

        var misses = Misses();
        var pending = songs
            .Where(s => !(misses.TryGetValue(s.Id, out var miss) && IsSettled(miss)) && !IsStored(s.Id))
            .Where(s => s.GetMediaStreams().All(m => m.Type != MediaStreamType.Lyric))
            .ToList();

        int found = 0, failures = 0;
        try
        {
            for (var i = 0; i < pending.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var lyrics = await LookUpAsync(pending[i], cancellationToken).ConfigureAwait(false);
                    found += lyrics.Lines.Count > 0 ? 1 : 0;
                    failures = 0;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogDebug(ex, "LRCLIB did not answer");
                    if (++failures >= MaxFailuresInARow)
                    {
                        _logger.LogWarning("LRCLIB is not answering; the search for lyrics will continue on its next run");
                        break;
                    }
                }

                if (i % SaveEvery == 0)
                {
                    SaveMisses();
                }

                progress.Report(100.0 * (i + 1) / pending.Count);
            }
        }
        finally
        {
            SaveMisses();
        }

        progress.Report(100);
        _logger.LogInformation("Lyrics search: {Found} found of {Pending} songs without lyrics", found, pending.Count);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static MusicLyrics Empty(bool instrumental) => new([], instrumental, false);

    private static bool IsSettled(Miss miss) => miss.Instrumental || DateTime.UtcNow - miss.At < _retryMissingAfter;

    private static bool HasText(LrclibRecord record)
        => !string.IsNullOrWhiteSpace(record.SyncedLyrics) || !string.IsNullOrWhiteSpace(record.PlainLyrics);

    private static int? Seconds(Audio song)
    {
        if (song.RunTimeTicks is not long ticks || ticks <= 0)
        {
            return null;
        }

        var seconds = (int)Math.Round(TimeSpan.FromTicks(ticks).TotalSeconds);
        return seconds is >= 1 and <= 3600 ? seconds : null;
    }

    private static string? MainArtist(Audio song)
    {
        var artist = song.Artists?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
            ?? song.AlbumArtists?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        return string.Equals(artist, "Various Artists", StringComparison.OrdinalIgnoreCase) ? null : artist;
    }

    /// <summary>Takes out what is not part of the title, like "(feat. Someone)" or "- Remastered 2011".</summary>
    private static string CleanTitle(string title)
    {
        var clean = BracketsRegex().Replace(title, " ");
        clean = VersionSuffixRegex().Replace(clean, string.Empty).Trim();
        return clean.Length > 0 ? clean : title;
    }

    private static string Comparable(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static string FirstLine(LrclibRecord record)
    {
        var text = record.PlainLyrics ?? record.SyncedLyrics ?? string.Empty;
        var line = text.Split('\n').Select(l => TimeTagRegex().Replace(l, string.Empty).Trim()).FirstOrDefault(l => l.Length > 0);
        return line ?? string.Empty;
    }

    /// <summary>Only lyrics of the same song: same title, same artist and practically the same length.</summary>
    private static LrclibRecord? BestMatch(IEnumerable<LrclibRecord> records, string title, string artist, int? seconds)
    {
        var wantedTitle = Comparable(CleanTitle(title));
        var wantedArtist = Comparable(artist);
        return records
            .Where(r => r.Instrumental || HasText(r))
            .Where(r => string.Equals(Comparable(CleanTitle(r.TrackName ?? string.Empty)), wantedTitle, StringComparison.Ordinal))
            .Where(r =>
            {
                var other = Comparable(r.ArtistName);
                return other.Length > 0 && (other.Contains(wantedArtist, StringComparison.Ordinal) || wantedArtist.Contains(other, StringComparison.Ordinal));
            })
            .Where(r => !seconds.HasValue || (r.Duration.HasValue && Math.Abs(r.Duration.Value - seconds.Value) <= MatchSeconds))
            .OrderByDescending(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics))
            .ThenBy(r => seconds.HasValue && r.Duration.HasValue ? Math.Abs(r.Duration.Value - seconds.Value) : 0)
            .FirstOrDefault();
    }

    private async Task<MusicLyrics?> ReadKnownAsync(Audio song, CancellationToken cancellationToken)
    {
        // What someone chose goes first, then what the library has, then what was found
        var chosen = await ReadStoredAsync(song.Id, true, cancellationToken).ConfigureAwait(false);
        if (chosen is not null)
        {
            return chosen;
        }

        try
        {
            var library = await _lyricManager.GetLyricsAsync(song, cancellationToken).ConfigureAwait(false);
            if (library?.Lyrics.Count > 0)
            {
                return new MusicLyrics(library.Lyrics, false, false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the lyrics file of {Song}", song.Id);
        }

        return await ReadStoredAsync(song.Id, false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MusicLyrics> LookUpAsync(Audio song, CancellationToken cancellationToken)
    {
        var record = await FindAsync(song, cancellationToken).ConfigureAwait(false);
        if (record is null || (!record.Instrumental && !HasText(record)))
        {
            Misses()[song.Id] = new Miss(song.Id, DateTime.UtcNow, false);
            return Empty(false);
        }

        if (record.Instrumental)
        {
            Misses()[song.Id] = new Miss(song.Id, DateTime.UtcNow, true);
            return Empty(true);
        }

        var lyrics = await KeepAsync(song.Id, record, false, cancellationToken).ConfigureAwait(false);
        if (lyrics is null)
        {
            Misses()[song.Id] = new Miss(song.Id, DateTime.UtcNow, false);
            return Empty(false);
        }

        Misses().TryRemove(song.Id, out _);
        return lyrics;
    }

    private async Task<LrclibRecord?> FindAsync(Audio song, CancellationToken cancellationToken)
    {
        var artist = MainArtist(song);
        if (string.IsNullOrWhiteSpace(song.Name) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var seconds = Seconds(song);
        if (seconds.HasValue)
        {
            var url = "get?track_name=" + Uri.EscapeDataString(song.Name)
                + "&artist_name=" + Uri.EscapeDataString(artist)
                + "&duration=" + seconds.Value.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(song.Album))
            {
                url += "&album_name=" + Uri.EscapeDataString(song.Album);
            }

            var exact = await RequestAsync<LrclibRecord>(url, cancellationToken).ConfigureAwait(false);
            if (exact is not null && (exact.Instrumental || HasText(exact)))
            {
                return exact;
            }
        }

        var records = await SearchRecordsAsync(song, cancellationToken).ConfigureAwait(false);
        return BestMatch(records, song.Name, artist, seconds);
    }

    private async Task<IReadOnlyList<LrclibRecord>> SearchRecordsAsync(Audio song, CancellationToken cancellationToken)
    {
        var artist = MainArtist(song);
        if (string.IsNullOrWhiteSpace(song.Name))
        {
            return [];
        }

        var url = "search?track_name=" + Uri.EscapeDataString(CleanTitle(song.Name));
        if (!string.IsNullOrWhiteSpace(artist))
        {
            url += "&artist_name=" + Uri.EscapeDataString(artist);
        }

        return await RequestAsync<List<LrclibRecord>>(url, cancellationToken).ConfigureAwait(false) ?? [];
    }

    /// <summary>One request at a time and well spaced, as LRCLIB asks.</summary>
    private async Task<T?> RequestAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _nextRequest - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _nextRequest = DateTime.UtcNow + _spacing;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            request.Headers.TryAddWithoutValidation("User-Agent", ClientName);
            using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter;
                var backOff = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? _defaultBackOff;
                _nextRequest = DateTime.UtcNow + (backOff > TimeSpan.Zero ? backOff : _defaultBackOff);
                throw new HttpRequestException("LRCLIB asked to wait", null, response.StatusCode);
            }

            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<T>(stream, _lrclibJson, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("LRCLIB sent an unexpected answer", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("LRCLIB took too long", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string StoredPath(Guid id, bool chosen, string extension)
    {
        var name = id.ToString("N", CultureInfo.InvariantCulture);
        return Path.Combine(StoreFolder, name[..2], name + (chosen ? ".chosen" : string.Empty) + extension);
    }

    private bool IsStored(Guid id)
        => File.Exists(StoredPath(id, true, ".lrc")) || File.Exists(StoredPath(id, true, ".txt"))
            || File.Exists(StoredPath(id, false, ".lrc")) || File.Exists(StoredPath(id, false, ".txt"));

    private async Task<MusicLyrics?> ReadStoredAsync(Guid id, bool chosen, CancellationToken cancellationToken)
    {
        foreach (var extension in new[] { ".lrc", ".txt" })
        {
            var path = StoredPath(id, chosen, extension);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var content = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                var lines = Parse(extension, content);
                if (lines?.Count > 0)
                {
                    return new MusicLyrics(lines, false, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not read the saved lyrics of {Song}", id);
            }
        }

        return null;
    }

    private IReadOnlyList<LyricLine>? Parse(string extension, string content)
    {
        var file = new LyricFile("lyric" + extension, content);
        foreach (var parser in _parsers)
        {
            var parsed = parser.ParseLyrics(file);
            if (parsed?.Lyrics.Count > 0)
            {
                return parsed.Lyrics;
            }
        }

        return null;
    }

    private async Task<MusicLyrics?> KeepAsync(Guid id, LrclibRecord record, bool chosen, CancellationToken cancellationToken)
    {
        var synced = !string.IsNullOrWhiteSpace(record.SyncedLyrics);
        var extension = synced ? ".lrc" : ".txt";
        var content = (synced ? record.SyncedLyrics : record.PlainLyrics)!.Trim();
        var lines = Parse(extension, content);
        if (lines is null || lines.Count == 0)
        {
            return null;
        }

        var path = StoredPath(id, chosen, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, true);

        var other = StoredPath(id, chosen, synced ? ".txt" : ".lrc");
        if (File.Exists(other))
        {
            File.Delete(other);
        }

        return new MusicLyrics(lines, false, true);
    }

    private ConcurrentDictionary<Guid, Miss> Misses()
    {
        var misses = Volatile.Read(ref _misses);
        if (misses is not null)
        {
            return misses;
        }

        lock (_missLock)
        {
            if (_misses is not null)
            {
                return _misses;
            }

            var loaded = new ConcurrentDictionary<Guid, Miss>();
            try
            {
                if (File.Exists(MissPath))
                {
                    using var stream = File.OpenRead(MissPath);
                    foreach (var miss in JsonSerializer.Deserialize<List<Miss>>(stream, JsonDefaults.Options) ?? [])
                    {
                        loaded[miss.Id] = miss;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "The list of songs without lyrics could not be read; it will be prepared again");
            }

            Volatile.Write(ref _misses, loaded);
            return loaded;
        }
    }

    private void SaveMisses()
    {
        lock (_missLock)
        {
            try
            {
                Directory.CreateDirectory(StoreFolder);
                var temporary = MissPath + ".tmp";
                using (var stream = File.Create(temporary))
                {
                    JsonSerializer.Serialize(stream, Misses().Values.ToList(), JsonDefaults.Options);
                }

                File.Move(temporary, MissPath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The list of songs without lyrics could not be saved");
            }
        }
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex BracketsRegex();

    [GeneratedRegex(@"\s+-\s+.*\b(remaster(ed)?|version|versión|mix|edit|live|en vivo|mono|stereo|bonus|deluxe)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffixRegex();

    [GeneratedRegex(@"^(\[[^\]]*\])+")]
    private static partial Regex TimeTagRegex();

    private sealed record Miss(Guid Id, DateTime At, bool Instrumental);

    private sealed record LrclibRecord(
        long Id,
        string? TrackName,
        string? ArtistName,
        string? AlbumName,
        double? Duration,
        bool Instrumental,
        string? PlainLyrics,
        string? SyncedLyrics);
}
