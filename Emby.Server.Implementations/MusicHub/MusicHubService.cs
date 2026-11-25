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
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MusicHub;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.MusicHub;

/// <summary>
/// Classifies the songs from what was measured in their sound and from what the library knows of them (genres, title,
/// artists and year); a song that was not listened to yet is classified with the second part only.
/// </summary>
public sealed class MusicHubService : IMusicHubService
{
    private const int FormatVersion = 1;
    private const int MinMoodTracks = 5;
    private const int MaxPerArtist = 3;
    private const int BatchSize = 400;

    private static readonly TimeSpan _folderCacheTime = TimeSpan.FromMinutes(10);
    private static readonly string[] _videoExtensions = [".m4v", ".mp4", ".mkv", ".webm", ".mov"];
    private static readonly string[] _songExtensions = [".m4a", ".mp3", ".flac", ".ogg", ".opus", ".wav", ".aac", ".wma", ".aiff", ".alac", ".ape"];

    // How intense and how happy each kind of music usually is
    private static readonly (string Word, double Energy, double Positivity)[] _genres =
    [
        ("metal", 0.95, 0.3), ("punk", 0.92, 0.45), ("hardcore", 0.95, 0.3), ("techno", 0.9, 0.55), ("trance", 0.85, 0.6),
        ("house", 0.85, 0.75), ("edm", 0.9, 0.75), ("dance", 0.88, 0.8), ("disco", 0.8, 0.85), ("electro", 0.8, 0.6),
        ("reggaeton", 0.88, 0.8), ("reguet", 0.88, 0.8), ("trap", 0.75, 0.45), ("hip hop", 0.72, 0.5), ("hip-hop", 0.72, 0.5),
        ("rap", 0.72, 0.5), ("salsa", 0.8, 0.85), ("merengue", 0.85, 0.88), ("bachata", 0.55, 0.6), ("cumbia", 0.75, 0.85),
        ("banda", 0.7, 0.7), ("norte", 0.65, 0.65), ("regional", 0.65, 0.6), ("ranchera", 0.5, 0.45), ("mariachi", 0.55, 0.55),
        ("latin", 0.72, 0.78), ("rock", 0.75, 0.55), ("grunge", 0.75, 0.3), ("alternative", 0.62, 0.45), ("alternativ", 0.62, 0.45),
        ("indie", 0.52, 0.5), ("k-pop", 0.78, 0.8), ("kpop", 0.78, 0.8), ("synth", 0.65, 0.6), ("pop", 0.65, 0.75),
        ("funk", 0.75, 0.8), ("reggae", 0.5, 0.75), ("r&b", 0.45, 0.58), ("rnb", 0.45, 0.58), ("soul", 0.42, 0.55),
        ("country", 0.5, 0.6), ("gospel", 0.5, 0.7), ("folk", 0.3, 0.5), ("singer", 0.3, 0.45), ("acoustic", 0.25, 0.55),
        ("acústic", 0.25, 0.55), ("balada", 0.3, 0.38), ("ballad", 0.3, 0.38), ("bolero", 0.28, 0.42), ("blues", 0.35, 0.3),
        ("jazz", 0.35, 0.55), ("bossa", 0.3, 0.65), ("lo-fi", 0.2, 0.5), ("lofi", 0.2, 0.5), ("chill", 0.2, 0.6),
        ("new age", 0.12, 0.55), ("ambient", 0.12, 0.45), ("classical", 0.2, 0.5), ("clásic", 0.2, 0.5), ("piano", 0.18, 0.45),
        ("instrumental", 0.25, 0.5), ("soundtrack", 0.35, 0.45), ("banda sonora", 0.35, 0.45), ("children", 0.55, 0.9), ("infantil", 0.55, 0.9)
    ];

    private static readonly string[] _sadWords = ["sad", "triste", "llor", "cry", "alone", "solo ", "goodbye", "adiós", "adios", "lonely", "dolor", "broken", "perd", "without you", "sin ti"];
    private static readonly string[] _partyWords = ["party", "fiesta", "dance", "baila", "perreo", "club", "celebr"];
    private static readonly string[] _loveWords = ["love", "amor", "amar", "corazón", "corazon", "heart", "romantic", "te quiero", "beso", "kiss", "darling", "baby"];
    private static readonly string[] _calmWords = ["acoustic", "acústic", "unplugged", "piano", "lullaby", "sleep", "interlude"];

    // The centre of each mood: how intense and how happy
    private static readonly (string Key, double Energy, double Positivity)[] _moods =
    [
        ("party", 0.9, 0.85), ("workout", 0.93, 0.52), ("happy", 0.62, 0.85), ("romantic", 0.4, 0.68),
        ("chill", 0.24, 0.62), ("focus", 0.14, 0.48), ("melancholy", 0.3, 0.22), ("intense", 0.82, 0.25)
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _paths;
    private readonly IMusicAudioAnalyzer _audio;
    private readonly ILogger<MusicHubService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, string[] Videos, string[] Songs)> _folders = new(StringComparer.Ordinal);
    private readonly object _loadLock = new();
    private Dictionary<Guid, Track>? _tracks;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicHubService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="paths">The application paths.</param>
    /// <param name="audio">The audio analyzer.</param>
    /// <param name="logger">The logger.</param>
    public MusicHubService(ILibraryManager libraryManager, IApplicationPaths paths, IMusicAudioAnalyzer audio, ILogger<MusicHubService> logger)
    {
        _libraryManager = libraryManager;
        _paths = paths;
        _audio = audio;
        _logger = logger;
    }

    private string StorePath => Path.Combine(_paths.DataPath, "music-analysis.json");

    /// <inheritdoc />
    public async Task BuildAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var songs = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true,
            IsVirtualItem = false
        }).OfType<Audio>().Where(a => a.GetType() == typeof(Audio) && a.ExtraType is null).ToList();

        var tracks = new List<Track>(songs.Count);
        for (var i = 0; i < songs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracks.Add(Analyze(songs[i], _audio.Get(songs[i].Id)));
            if (i % 200 == 0)
            {
                progress.Report(90.0 * i / Math.Max(1, songs.Count));
            }
        }

        var temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var stream = File.Create(temporary);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, new Store(FormatVersion, tracks), JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, StorePath, true);
        Volatile.Write(ref _tracks, tracks.ToDictionary(t => t.Id));
        progress.Report(100);
        _logger.LogInformation("Music analyzed: {Songs} songs in {Milliseconds} ms", tracks.Count, (long)(DateTime.UtcNow - started).TotalMilliseconds);
    }

    /// <inheritdoc />
    public IReadOnlyList<MusicMood> GetMoods()
    {
        var counts = Tracks().Values.GroupBy(t => t.Mood).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return _moods
            .Select(m => new MusicMood(m.Key, counts.GetValueOrDefault(m.Key)))
            .Where(m => m.Count >= MinMoodTracks)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetMoodTracks(User user, string mood, Guid? parentId, int limit)
    {
        var centre = _moods.FirstOrDefault(m => string.Equals(m.Key, mood, StringComparison.Ordinal));
        if (centre.Key is null)
        {
            return [];
        }

        // Close to the mood, but not always the same songs in the same order
        var candidates = Tracks().Values
            .Where(t => string.Equals(t.Mood, centre.Key, StringComparison.Ordinal))
            .OrderBy(t => Distance(t.Energy, t.Positivity, centre.Energy, centre.Positivity) + (Random.Shared.NextDouble() * 0.25))
            .Select(t => t.Id);
        return Visible(user, candidates, parentId, limit);
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetSimilar(User user, Guid itemId, int limit)
    {
        var tracks = Tracks();
        if (!tracks.TryGetValue(itemId, out var song))
        {
            return [];
        }

        var perArtist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ranked = new List<Guid>();
        var sound = _audio.Get(itemId);
        foreach (var other in tracks.Values.Where(t => t.Id != itemId).OrderByDescending(t => Similarity(song, t, sound, _audio.Get(t.Id))))
        {
            // A mix of artists, not the whole album of the same one
            var artist = other.Artists.FirstOrDefault() ?? string.Empty;
            var used = perArtist.GetValueOrDefault(artist);
            if (used >= MaxPerArtist)
            {
                continue;
            }

            perArtist[artist] = used + 1;
            ranked.Add(other.Id);
            if (ranked.Count >= limit * 3)
            {
                break;
            }
        }

        return Visible(user, ranked, null, limit);
    }

    /// <inheritdoc />
    public MusicAnalysis? GetAnalysis(Guid itemId)
        => Tracks().TryGetValue(itemId, out var t) ? new MusicAnalysis(t.Energy, t.Positivity, t.Mood, t.Genres, t.Year, _audio.Get(itemId)) : null;

    /// <inheritdoc />
    public string? FindVideo(Audio song)
    {
        var folder = Path.GetDirectoryName(song.Path);
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        var (videos, songs) = FilesIn(folder);
        return MusicVideoNames.Find(song.Path, videos, songs);
    }

    private static Track Analyze(Audio song, MusicAudioFeatures? sound)
    {
        var genres = song.Genres ?? [];
        var hits = genres
            .SelectMany(g => _genres.Where(k => g.Contains(k.Word, StringComparison.OrdinalIgnoreCase)).Take(1))
            .ToList();
        var energy = hits.Count > 0 ? hits.Average(h => h.Energy) : 0.5;
        var positivity = hits.Count > 0 ? hits.Average(h => h.Positivity) : 0.5;

        var title = song.Name ?? string.Empty;
        if (HasWord(title, _calmWords))
        {
            energy -= 0.2;
        }

        if (title.Contains("remix", StringComparison.OrdinalIgnoreCase))
        {
            energy += 0.15;
        }

        if (HasWord(title, _partyWords))
        {
            energy += 0.12;
            positivity += 0.12;
        }

        if (HasWord(title, _sadWords))
        {
            energy -= 0.05;
            positivity -= 0.22;
        }

        var romantic = HasWord(title, _loveWords);
        if (romantic)
        {
            energy = (energy + 0.4) / 2;
            positivity = (positivity + 0.68) / 2;
        }

        // What the song really sounds like weighs more than what its labels say
        if (sound is not null)
        {
            energy = (energy * 0.25) + (sound.Energy * 0.75);
            positivity = (positivity * 0.4) + (sound.Positivity * 0.6);
        }

        energy = Math.Round(Math.Clamp(energy, 0, 1), 3);
        positivity = Math.Round(Math.Clamp(positivity, 0, 1), 3);
        var mood = romantic && energy < 0.72 ? "romantic" : _moods.MinBy(m => Distance(energy, positivity, m.Energy, m.Positivity)).Key;
        var artists = song.Artists?.Count > 0 ? song.Artists : song.AlbumArtists;
        return new Track(song.Id, artists?.ToArray() ?? [], genres, song.ProductionYear, energy, positivity, mood);
    }

    private static bool HasWord(string text, string[] words)
        => Array.Exists(words, w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static double Distance(double energyA, double positivityA, double energyB, double positivityB)
        => Math.Sqrt(Math.Pow(energyA - energyB, 2) + Math.Pow(positivityA - positivityB, 2));

    /// <summary>Whether two keys sound well together: the same one, a neighbour on the wheel, or its relative.</summary>
    private static bool KeysMatch(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        var numberA = int.Parse(a.AsSpan(0, a.Length - 1), CultureInfo.InvariantCulture);
        var numberB = int.Parse(b.AsSpan(0, b.Length - 1), CultureInfo.InvariantCulture);
        var apart = Math.Abs(numberA - numberB);
        return a[^1] == b[^1] ? apart is 1 or 11 : apart == 0;
    }

    private static double SoundSimilarity(MusicAudioFeatures a, MusicAudioFeatures b)
    {
        double tempo = 0;
        if (a.Bpm is double bpmA && b.Bpm is double bpmB)
        {
            // Half or double the speed also mixes well
            var ratio = Math.Abs(Math.Log2(bpmA / bpmB));
            tempo = Math.Max(0, 1 - (Math.Min(ratio, Math.Abs(ratio - 1)) / 0.12));
        }

        var character = 1 - ((Math.Abs(a.Energy - b.Energy) + Math.Abs(a.Danceability - b.Danceability) + Math.Abs(a.Brightness - b.Brightness) + Math.Abs(a.Acoustic - b.Acoustic)) / 4);
        return (tempo * 1.4) + (KeysMatch(a.Camelot, b.Camelot) ? 0.9 : 0) + (character * 2.2);
    }

    private static double Similarity(Track a, Track b, MusicAudioFeatures? soundA, MusicAudioFeatures? soundB)
    {
        var sharedGenres = a.Genres.Count(g => b.Genres.Contains(g, StringComparer.OrdinalIgnoreCase));
        var genres = a.Genres.Length > 0 ? (double)sharedGenres / a.Genres.Length : 0;
        var sameArtist = a.Artists.Intersect(b.Artists, StringComparer.OrdinalIgnoreCase).Any() ? 1 : 0;
        var era = a.Year.HasValue && b.Year.HasValue ? Math.Max(0, 1 - (Math.Abs(a.Year.Value - b.Year.Value) / 15.0)) : 0;
        var sound = soundA is not null && soundB is not null ? SoundSimilarity(soundA, soundB) : 0;
        return (genres * 3) + (sameArtist * 1.2) + (era * 0.6) + ((1 - Distance(a.Energy, a.Positivity, b.Energy, b.Positivity)) * 2) + sound;
    }

    private (string[] Videos, string[] Songs) FilesIn(string folder)
    {
        var now = DateTime.UtcNow;
        if (_folders.TryGetValue(folder, out var known) && now - known.At < _folderCacheTime)
        {
            return (known.Videos, known.Songs);
        }

        string[] videos;
        string[] songs;
        try
        {
            var files = Directory.EnumerateFiles(folder).ToArray();
            videos = files.Where(f => _videoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToArray();
            songs = files.Where(f => _songExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the folder {Folder}", folder);
            videos = [];
            songs = [];
        }

        _folders[folder] = (now, videos, songs);
        return (videos, songs);
    }

    private List<BaseItem> Visible(User user, IEnumerable<Guid> ids, Guid? parentId, int limit)
    {
        var result = new List<BaseItem>(limit);
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            var query = new InternalItemsQuery(user)
            {
                ItemIds = chunk,
                IncludeItemTypes = [BaseItemKind.Audio],
                Recursive = true
            };
            if (parentId.HasValue)
            {
                query.AncestorIds = [parentId.Value];
            }

            // The database does not keep the order that was asked
            var found = _libraryManager.GetItemList(query).ToDictionary(i => i.Id);
            result.AddRange(chunk.Where(found.ContainsKey).Select(id => found[id]).Take(limit - result.Count));
            if (result.Count >= limit)
            {
                break;
            }
        }

        return result;
    }

    private Dictionary<Guid, Track> Tracks()
    {
        var tracks = Volatile.Read(ref _tracks);
        if (tracks is not null)
        {
            return tracks;
        }

        lock (_loadLock)
        {
            if (_tracks is not null)
            {
                return _tracks;
            }

            Dictionary<Guid, Track> loaded = [];
            try
            {
                if (File.Exists(StorePath))
                {
                    using var stream = File.OpenRead(StorePath);
                    var store = JsonSerializer.Deserialize<Store>(stream, JsonDefaults.Options);
                    if (store?.Version == FormatVersion)
                    {
                        loaded = store.Tracks.ToDictionary(t => t.Id);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "The music analysis could not be read; the scheduled task will prepare it again");
            }

            Volatile.Write(ref _tracks, loaded);
            return loaded;
        }
    }

    private sealed record Track(Guid Id, string[] Artists, string[] Genres, int? Year, double Energy, double Positivity, string Mood);

    private sealed record Store(int Version, List<Track> Tracks);
}
