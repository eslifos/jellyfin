using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.MediaSegments;

/// <summary>
/// Stores intro / credits analysis results in a JSON file inside the data folder,
/// and the cached audio fingerprints inside the cache folder.
/// </summary>
public sealed class IntroCreditsStore : IDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<IntroCreditsStore> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Lock _loadLock = new();
    private ConcurrentDictionary<Guid, IntroCreditsRecord>? _records;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroCreditsStore"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public IntroCreditsStore(IApplicationPaths appPaths, ILogger<IntroCreditsStore> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// Gets the folder where audio fingerprints are cached.
    /// </summary>
    public string FingerprintFolder => Path.Combine(_appPaths.CachePath, "intro-credits");

    private string StorePath => Path.Combine(_appPaths.DataPath, "intro-credits.json");

    private ConcurrentDictionary<Guid, IntroCreditsRecord> Records
    {
        get
        {
            if (_records is not null)
            {
                return _records;
            }

            lock (_loadLock)
            {
                if (_records is not null)
                {
                    return _records;
                }

                var records = new ConcurrentDictionary<Guid, IntroCreditsRecord>();
                try
                {
                    if (File.Exists(StorePath))
                    {
                        var data = JsonSerializer.Deserialize<ConcurrentDictionary<Guid, IntroCreditsRecord>>(File.ReadAllText(StorePath), _jsonOptions);
                        if (data is not null)
                        {
                            records = data;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    _logger.LogError(ex, "Could not read intro / credits results from {Path}", StorePath);
                }

                _records = records;
                return records;
            }
        }
    }

    /// <summary>
    /// Gets the record of an item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The record, or null when the item was never analyzed.</returns>
    public IntroCreditsRecord? Get(Guid itemId) => Records.TryGetValue(itemId, out var record) ? record : null;

    /// <summary>
    /// Sets the record of an item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="record">The record.</param>
    public void Set(Guid itemId, IntroCreditsRecord record) => Records[itemId] = record;

    /// <summary>
    /// Removes the record of an item and its cached fingerprints.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns><c>true</c> when a record was removed.</returns>
    public bool Remove(Guid itemId)
    {
        foreach (var kind in new[] { "intro", "credits" })
        {
            var path = GetFingerprintPath(itemId, kind);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not delete fingerprint {Path}", path);
            }
        }

        return Records.TryRemove(itemId, out _);
    }

    /// <summary>
    /// Removes every record and cached fingerprint.
    /// </summary>
    public void Clear()
    {
        Records.Clear();
        try
        {
            if (Directory.Exists(FingerprintFolder))
            {
                Directory.Delete(FingerprintFolder, true);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not clear fingerprint cache {Path}", FingerprintFolder);
        }
    }

    /// <summary>
    /// Gets the number of analyzed items and how many have an intro / credits.
    /// </summary>
    /// <returns>The counters.</returns>
    public (int Analyzed, int WithIntro, int WithCredits) GetCounts()
    {
        int analyzed = 0, intro = 0, credits = 0;
        foreach (var record in Records.Values)
        {
            analyzed++;
            if (record.IntroStartTicks.HasValue)
            {
                intro++;
            }

            if (record.CreditsStartTicks.HasValue)
            {
                credits++;
            }
        }

        return (analyzed, intro, credits);
    }

    /// <summary>
    /// Gets the cache path of a fingerprint.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="kind">Either intro or credits.</param>
    /// <returns>The file path.</returns>
    public string GetFingerprintPath(Guid itemId, string kind)
        => Path.Combine(FingerprintFolder, $"{itemId:N}-{kind}.bin");

    /// <summary>
    /// Writes the records to disk.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tmp = StorePath + ".tmp";
            var stream = File.Create(tmp);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, Records, _jsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tmp, StorePath, true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _saveLock.Dispose();
}
