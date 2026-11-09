using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.Manager;

/// <summary>
/// Stores the per-series main metadata source in "metadata-sources.json" (data folder).
/// </summary>
public class MetadataSourcePreferences : IMetadataSourcePreferences
{
    private readonly string _path;
    private readonly ILogger<MetadataSourcePreferences> _logger;
    private readonly Lock _lock = new();
    private Dictionary<Guid, string>? _map;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataSourcePreferences"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public MetadataSourcePreferences(IApplicationPaths appPaths, ILogger<MetadataSourcePreferences> logger)
    {
        _path = Path.Combine(appPaths.DataPath, "metadata-sources.json");
        _logger = logger;
    }

    /// <inheritdoc />
    public string? GetPrimarySource(BaseItem item)
    {
        var seriesId = item switch
        {
            Series s => s.Id,
            Season season => season.SeriesId,
            Episode episode => episode.SeriesId,
            _ => Guid.Empty
        };

        return seriesId.Equals(Guid.Empty) ? null : GetPrimarySource(seriesId);
    }

    /// <inheritdoc />
    public string? GetPrimarySource(Guid seriesId)
    {
        lock (_lock)
        {
            return Load().GetValueOrDefault(seriesId);
        }
    }

    /// <inheritdoc />
    public void SetPrimarySource(Guid seriesId, string? source)
    {
        lock (_lock)
        {
            var map = Load();
            if (string.IsNullOrWhiteSpace(source))
            {
                map.Remove(seriesId);
            }
            else
            {
                map[seriesId] = source.Trim();
            }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(map));
            File.Move(tmp, _path, true);
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, string> GetAll()
    {
        lock (_lock)
        {
            return Load().ToDictionary(kv => kv.Key, kv => kv.Value);
        }
    }

    private Dictionary<Guid, string> Load()
    {
        if (_map is not null)
        {
            return _map;
        }

        try
        {
            _map = File.Exists(_path) ?
                JsonSerializer.Deserialize<Dictionary<Guid, string>>(File.ReadAllText(_path)) ?? [] :
                [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogError(ex, "Could not read {Path}", _path);
            _map = [];
        }

        return _map;
    }
}
