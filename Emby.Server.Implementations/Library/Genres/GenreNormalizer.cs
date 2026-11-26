using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Genres;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Library.Genres;

/// <summary>
/// Keeps the genres of each library in its language with a map of equivalences: the built-in one
/// (<see cref="GenreDefaults"/>) or the one an administrator saved for that language (genre-map.json in the data
/// folder). Music keeps its genres as they are: they are styles, not translatable names.
/// </summary>
public sealed class GenreNormalizer : IGenreNormalizer
{
    private const int MaxEntries = 1000;
    private const int MaxNameLength = 100;

    private readonly IApplicationPaths _paths;
    private readonly ILogger<GenreNormalizer> _logger;
    private readonly object _lock = new();
    private Dictionary<string, Dictionary<string, string>>? _custom;
    private Dictionary<string, IReadOnlyDictionary<string, string>> _lookups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="GenreNormalizer"/> class.
    /// </summary>
    /// <param name="paths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public GenreNormalizer(IApplicationPaths paths, ILogger<GenreNormalizer> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    private string MapPath => Path.Combine(_paths.DataPath, "genre-map.json");

    /// <inheritdoc />
    public GenreMap GetMap(IReadOnlyCollection<string> languagesInUse)
    {
        var custom = LoadCustom();
        var inUse = new HashSet<string>(languagesInUse.Select(NormalizeLanguage).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var languages = GenreDefaults.ByLanguage.Keys
            .Concat(custom.Keys)
            .Concat(inUse)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(l => !string.Equals(l, "en", StringComparison.OrdinalIgnoreCase) || custom.ContainsKey(l))
            .OrderBy(l => inUse.Contains(l) ? 0 : 1)
            .ThenBy(l => l, StringComparer.OrdinalIgnoreCase)
            .Select(language =>
            {
                GenreDefaults.ByLanguage.TryGetValue(language, out var defaults);
                var isCustom = custom.TryGetValue(language, out var saved);
                var entries = (isCustom ? saved! : (IReadOnlyDictionary<string, string>?)defaults ?? new Dictionary<string, string>())
                    .Select(e => new GenreMapEntry(
                        e.Key,
                        e.Value,
                        defaults is not null && defaults.TryGetValue(e.Key, out var d) && string.Equals(d, e.Value, StringComparison.Ordinal)))
                    .OrderBy(e => e.From, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new GenreMapLanguage(language.ToLowerInvariant(), entries, isCustom, inUse.Contains(language));
            })
            .ToList();
        return new GenreMap(languages);
    }

    /// <inheritdoc />
    public void SetMap(string language, IReadOnlyList<GenreMapPair> entries)
    {
        var code = NormalizeLanguage(language) ?? throw new ArgumentException("Not a language code", nameof(language));
        if (entries.Count > MaxEntries)
        {
            throw new ArgumentException("Too many equivalences", nameof(entries));
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var from = entry.From?.Trim() ?? string.Empty;
            var to = entry.To?.Trim() ?? string.Empty;
            if (from.Length == 0 || to.Length == 0 || from.Length > MaxNameLength || to.Length > MaxNameLength)
            {
                throw new ArgumentException("Every equivalence needs both names, of at most " + MaxNameLength + " characters", nameof(entries));
            }

            map[from] = to;
        }

        lock (_lock)
        {
            var custom = new Dictionary<string, Dictionary<string, string>>(LoadCustom(), StringComparer.OrdinalIgnoreCase)
            {
                [code] = map
            };
            SaveCustom(custom);
        }

        _logger.LogInformation("Genre map of {Language} saved with {Count} equivalences", code, map.Count);
    }

    /// <inheritdoc />
    public void ResetMap(string language)
    {
        var code = NormalizeLanguage(language) ?? throw new ArgumentException("Not a language code", nameof(language));
        lock (_lock)
        {
            var custom = new Dictionary<string, Dictionary<string, string>>(LoadCustom(), StringComparer.OrdinalIgnoreCase);
            if (custom.Remove(code))
            {
                SaveCustom(custom);
            }
        }

        _logger.LogInformation("Genre map of {Language} back to the built-in equivalences", code);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> GetSourceGenres()
    {
        var custom = LoadCustom();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in GenreDefaults.ByLanguage.Keys.Concat(custom.Keys))
        {
            foreach (var entry in EntriesOf(language, custom))
            {
                names.Add(entry.Key);
            }
        }

        return names;
    }

    /// <inheritdoc />
    public bool Normalize(BaseItem item)
    {
        if (item.Genres is not { Length: > 0 } genres || !IsMapped(item))
        {
            return false;
        }

        var language = NormalizeLanguage(item.GetPreferredMetadataLanguage());
        if (language is null)
        {
            return false;
        }

        var lookup = LookupOf(language);
        if (lookup.Count == 0)
        {
            return false;
        }

        var mapped = GenreMapper.Map(genres, lookup);
        if (mapped.AsSpan().SequenceEqual(genres))
        {
            return false;
        }

        item.Genres = mapped;
        return true;
    }

    /// <summary>
    /// Music (styles, not translatable names), the genre items themselves and locked genres stay as they are.
    /// </summary>
    private static bool IsMapped(BaseItem item)
        => item is not (MediaBrowser.Controller.Entities.Audio.Audio or MusicAlbum or MusicArtist or MusicVideo or MusicGenre or Genre)
            && !item.IsLocked
            && !item.LockedFields.Contains(MetadataField.Genres);

    /// <summary>The language part of a code ("es-MX" → "es"), or null when it is not one.</summary>
    private static string? NormalizeLanguage(string? language)
    {
        var code = language?.Trim().Split('-', '_')[0].ToLowerInvariant() ?? string.Empty;
        return code.Length is 2 or 3 && code.All(c => c is >= 'a' and <= 'z') ? code : null;
    }

    private static IEnumerable<KeyValuePair<string, string>> EntriesOf(string language, Dictionary<string, Dictionary<string, string>> custom)
    {
        if (custom.TryGetValue(language, out var saved))
        {
            return saved;
        }

        return GenreDefaults.ByLanguage.TryGetValue(language, out var defaults) ? defaults : [];
    }

    private IReadOnlyDictionary<string, string> LookupOf(string language)
    {
        lock (_lock)
        {
            if (!_lookups.TryGetValue(language, out var lookup))
            {
                lookup = GenreMapper.BuildLookup(EntriesOf(language, LoadCustom()));
                _lookups[language] = lookup;
            }

            return lookup;
        }
    }

    private Dictionary<string, Dictionary<string, string>> LoadCustom()
    {
        lock (_lock)
        {
            if (_custom is not null)
            {
                return _custom;
            }

            try
            {
                if (File.Exists(MapPath))
                {
                    using var stream = File.OpenRead(MapPath);
                    var saved = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream, JsonDefaults.Options);
                    _custom = saved?.ToDictionary(
                        l => l.Key,
                        l => new Dictionary<string, string>(l.Value, StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The genre map could not be read; the built-in equivalences are used");
            }

            _custom ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            return _custom;
        }
    }

    private void SaveCustom(Dictionary<string, Dictionary<string, string>> custom)
    {
        Directory.CreateDirectory(_paths.DataPath);
        var temporary = MapPath + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, custom, JsonDefaults.Options);
        }

        File.Move(temporary, MapPath, true);
        _custom = custom;
        _lookups = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    }
}
