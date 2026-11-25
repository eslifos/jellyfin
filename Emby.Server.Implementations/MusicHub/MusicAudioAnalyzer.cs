using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MusicHub;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.MusicHub;

/// <summary>
/// Decodes each song with FFmpeg, measures it and keeps the result, so only new or changed songs are listened to.
/// </summary>
public sealed class MusicAudioAnalyzer : IMusicAudioAnalyzer
{
    private const int SaveEvery = 20;
    private const int BufferSize = 64 * 1024;
    private const int MaxListenSeconds = 900;

    private static readonly TimeSpan _songTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _retryFailedAfter = TimeSpan.FromDays(14);

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<MusicAudioAnalyzer> _logger;
    private readonly object _storeLock = new();
    private ConcurrentDictionary<Guid, Entry>? _entries;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicAudioAnalyzer"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaEncoder">The media encoder.</param>
    /// <param name="paths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public MusicAudioAnalyzer(ILibraryManager libraryManager, IMediaEncoder mediaEncoder, IApplicationPaths paths, ILogger<MusicAudioAnalyzer> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _paths = paths;
        _logger = logger;
    }

    private string StorePath => Path.Combine(_paths.DataPath, "music-audio.json");

    /// <inheritdoc />
    public MusicAudioFeatures? Get(Guid itemId)
        => Entries().TryGetValue(itemId, out var entry) ? entry.Features : null;

    /// <inheritdoc />
    public async Task AnalyzeAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var entries = Entries();
        var now = DateTime.UtcNow;
        var pending = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true,
            IsVirtualItem = false
        }).OfType<Audio>()
            .Where(a => a.GetType() == typeof(Audio) && a.ExtraType is null && a.IsFileProtocol)
            .Where(a => !entries.TryGetValue(a.Id, out var known)
                || known.Stamp != a.DateModified.Ticks
                || (known.Features is null && now - known.At > _retryFailedAfter))
            .Select(a => (a.Id, a.Path, Stamp: a.DateModified.Ticks))
            .ToList();

        int done = 0, measured = 0, failed = 0;
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 3)
        };

        try
        {
            await Parallel.ForEachAsync(pending, options, async (song, token) =>
            {
                // A disconnected drive is not a bad song: it is tried again on the next run
                if (File.Exists(song.Path))
                {
                    var features = await ListenSafelyAsync(song.Path, token).ConfigureAwait(false);
                    entries[song.Id] = new Entry(song.Id, song.Stamp, DateTime.UtcNow, features);
                    Interlocked.Increment(ref (features is null ? ref failed : ref measured));
                }

                var count = Interlocked.Increment(ref done);
                progress.Report(100.0 * count / pending.Count);
                if (count % SaveEvery == 0)
                {
                    Save();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            if (pending.Count > 0)
            {
                Save();
            }
        }

        progress.Report(100);
        _logger.LogInformation("Music listened to: {Measured} measured, {Failed} could not be read, {Pending} pending at the start", measured, failed, pending.Count);
    }

    private async Task<MusicAudioFeatures?> ListenSafelyAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_songTimeout);
        try
        {
            return await ListenAsync(path, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Listening to {Path} took too long", path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not listen to {Path}", path);
            return null;
        }
    }

    private async Task<MusicAudioFeatures?> ListenAsync(string path, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-v", "error", "-threads", "1", "-t", MaxListenSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", path, "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1", "-ar", AudioFeatureExtractor.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1" })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        process.Start();
        try
        {
            // Playback goes first
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Could not lower the priority of the analysis");
        }

        try
        {
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            var extractor = new AudioFeatureExtractor();
            var buffer = new byte[BufferSize];
            var stream = process.StandardOutput.BaseStream;
            var left = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(left), cancellationToken).ConfigureAwait(false)) > 0)
            {
                var total = left + read;
                var usable = total - (total % sizeof(float));
                extractor.Add(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, usable)));
                left = total - usable;
                Array.Copy(buffer, usable, buffer, 0, left);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var message = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _logger.LogDebug("FFmpeg could not read {Path}: {Message}", path, message);
                return null;
            }

            return extractor.Finish();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
    }

    private ConcurrentDictionary<Guid, Entry> Entries()
    {
        var entries = Volatile.Read(ref _entries);
        if (entries is not null)
        {
            return entries;
        }

        lock (_storeLock)
        {
            if (_entries is not null)
            {
                return _entries;
            }

            var loaded = new ConcurrentDictionary<Guid, Entry>();
            try
            {
                if (File.Exists(StorePath))
                {
                    using var stream = File.OpenRead(StorePath);
                    foreach (var entry in JsonSerializer.Deserialize<List<Entry>>(stream, JsonDefaults.Options) ?? [])
                    {
                        loaded[entry.Id] = entry;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "The audio analysis could not be read; the songs will be listened to again");
            }

            Volatile.Write(ref _entries, loaded);
            return loaded;
        }
    }

    private void Save()
    {
        lock (_storeLock)
        {
            try
            {
                var temporary = StorePath + ".tmp";
                using (var stream = File.Create(temporary))
                {
                    JsonSerializer.Serialize(stream, Entries().Values.ToList(), JsonDefaults.Options);
                }

                File.Move(temporary, StorePath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The audio analysis could not be saved");
            }
        }
    }

    private sealed record Entry(Guid Id, long Stamp, DateTime At, MusicAudioFeatures? Features);
}
