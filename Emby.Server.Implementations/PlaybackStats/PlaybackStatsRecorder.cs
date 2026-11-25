using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.PlaybackStats;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Emby.Server.Implementations.PlaybackStats;

/// <summary>
/// Listens to playback events and records every play in <see cref="PlaybackStatsStore"/>.
/// Only time actually watched is counted (pauses and long gaps are excluded).
/// </summary>
public sealed class PlaybackStatsRecorder : IHostedService, IDisposable
{
    /// <summary>Plays shorter than this (and not finished) are discarded.</summary>
    private static readonly TimeSpan _minimumPlay = TimeSpan.FromSeconds(20);

    /// <summary>Longest gap between two progress reports that still counts as watching.</summary>
    private static readonly TimeSpan _maxGap = TimeSpan.FromMinutes(2);

    /// <summary>How often a running play is written to disk.</summary>
    private static readonly TimeSpan _saveInterval = TimeSpan.FromSeconds(60);

    /// <summary>The library is still waking up right after the server starts.</summary>
    private static readonly TimeSpan _classifyDelay = TimeSpan.FromMinutes(1);

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly PlaybackStatsStore _store;
    private readonly ILogger<PlaybackStatsRecorder> _logger;
    private readonly ConcurrentDictionary<string, ActivePlay> _active = new();
    private readonly ConcurrentDictionary<string, Guid> _artists = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStatsRecorder"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="store">The store.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackStatsRecorder(ISessionManager sessionManager, ILibraryManager libraryManager, PlaybackStatsStore store, ILogger<PlaybackStatsRecorder> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _store.CloseOrphans();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open the playback statistics database");
        }

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _ = Task.Run(() => ClassifyOlderPlaysAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;

        foreach (var key in _active.Keys)
        {
            if (_active.TryRemove(key, out var play))
            {
                Finish(play, DateTime.UtcNow, null, false);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
    }

    private static string? Key(PlaybackProgressEventArgs e)
        => e.Item is null || e.Session is null ? null : e.Session.Id + "|" + e.Item.Id.ToString("N");

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) => Safe(() => Begin(e));

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e) => Safe(() =>
    {
        var key = Key(e);
        if (key is null)
        {
            return;
        }

        // Started before the server came up: begin tracking now.
        if (!_active.TryGetValue(key, out var play))
        {
            Begin(e);
            return;
        }

        lock (play)
        {
            var now = DateTime.UtcNow;
            Accumulate(play, now);
            play.IsPaused = e.IsPaused;
            play.Entry.PositionTicks = e.PlaybackPositionTicks ?? play.Entry.PositionTicks;
            ApplyStream(play.Entry, e.Session);
            if (now - play.LastSavedUtc >= _saveInterval && play.Entry.Id > 0)
            {
                _store.Update(play.Entry);
                play.LastSavedUtc = now;
            }
        }
    });

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e) => Safe(() =>
    {
        var key = Key(e);
        if (key is null || !_active.TryRemove(key, out var play))
        {
            return;
        }

        Finish(play, DateTime.UtcNow, e.PlaybackPositionTicks, e.PlayedToCompletion);
    });

    private void Begin(PlaybackProgressEventArgs e)
    {
        var key = Key(e);
        if (key is null || e.Item is null)
        {
            return;
        }

        var user = e.Users?.Count > 0 ? e.Users[0] : null;
        var userId = user?.Id ?? e.Session.UserId;
        if (userId.Equals(Guid.Empty))
        {
            return;
        }

        var item = e.Item;
        var episode = item as Episode;
        var entry = new PlaybackHistoryEntry
        {
            UserId = userId,
            UserName = user?.Username ?? e.Session.UserName,
            ItemId = item.Id,
            ItemName = item.Name,
            ItemType = item.GetBaseItemKind().ToString(),
            SeriesId = episode?.SeriesId,
            SeriesName = episode?.SeriesName,
            ParentIndexNumber = episode?.ParentIndexNumber,
            IndexNumber = item.IndexNumber,
            ProductionYear = item.ProductionYear,
            RunTimeTicks = item.RunTimeTicks,
            StartedUtc = DateTime.UtcNow,
            PositionTicks = e.PlaybackPositionTicks,
            Client = e.ClientName ?? e.Session.Client,
            DeviceName = e.DeviceName ?? e.Session.DeviceName,
            RemoteAddress = e.Session.RemoteEndPoint
        };
        ApplyStream(entry, e.Session);
        Describe(item, entry);
        entry.Id = _store.Insert(entry);

        var play = new ActivePlay(entry) { IsPaused = e.IsPaused, LastTickUtc = entry.StartedUtc, LastSavedUtc = entry.StartedUtc };
        _active[key] = play;
    }

    /// <summary>Says which kind of library the item is from and, for music and books, its album and artist.</summary>
    private void Describe(BaseItem item, PlaybackHistoryEntry entry)
    {
        entry.Category = PlaybackCategory.FromItemType(item.GetBaseItemKind().ToString());
        try
        {
            switch (item)
            {
                case AudioItem audio:
                    // A book stored as a folder of parts is made of plain audio files inside a books library
                    if (audio is AudioBook || _libraryManager.GetInheritedContentType(audio) == CollectionType.books)
                    {
                        entry.Category = PlaybackCategory.Book;
                    }

                    var album = audio.AlbumEntity;
                    entry.AlbumId = album?.Id;
                    entry.AlbumName = album?.Name ?? audio.Album;
                    entry.ArtistName = audio.AlbumArtists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
                        ?? audio.Artists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
                    entry.ArtistId = FindArtist(entry.ArtistName);
                    break;
                case MusicVideo video:
                    entry.ArtistName = video.Artists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
                    entry.ArtistId = FindArtist(entry.ArtistName);
                    break;
            }
        }
        catch (Exception ex)
        {
            // The play still counts, only without its album or artist
            _logger.LogDebug(ex, "Could not describe item {Id} for the statistics", item.Id);
        }
    }

    private Guid? FindArtist(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (_artists.TryGetValue(name, out var known))
        {
            return known;
        }

        var found = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.MusicArtist],
            Name = name,
            Limit = 1
        }).FirstOrDefault();
        if (found is null)
        {
            return null;
        }

        _artists[name] = found.Id;
        return found.Id;
    }

    /// <summary>The plays recorded before categories existed get theirs once; items no longer in the library keep their type.</summary>
    private async Task ClassifyOlderPlaysAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_classifyDelay, cancellationToken).ConfigureAwait(false);
            foreach (var id in _store.GetUnclassifiedItems())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_libraryManager.GetItemById(id) is { } item)
                {
                    var described = new PlaybackHistoryEntry();
                    Describe(item, described);
                    _store.SetClassification(id, described);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The server is stopping: the rest is done on the next start
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not classify the older playback statistics");
        }
    }

    private void Finish(ActivePlay play, DateTime now, long? positionTicks, bool playedToCompletion)
    {
        lock (play)
        {
            Accumulate(play, now);
            var entry = play.Entry;
            entry.StoppedUtc = now;
            entry.PositionTicks = positionTicks ?? entry.PositionTicks;
            var runtime = entry.RunTimeTicks ?? 0;
            entry.Completed = playedToCompletion || (runtime > 0 && entry.PositionTicks >= runtime * 0.9);

            if (entry.Id <= 0)
            {
                return;
            }

            if (!entry.Completed && entry.WatchedSeconds < _minimumPlay.TotalSeconds)
            {
                _store.Delete(entry.Id);
            }
            else
            {
                _store.Update(entry);
            }
        }
    }

    /// <summary>
    /// Adds the time since the last report, unless the player was paused or the gap is too long.
    /// </summary>
    private static void Accumulate(ActivePlay play, DateTime now)
    {
        var delta = now - play.LastTickUtc;
        if (!play.IsPaused && delta > TimeSpan.Zero && delta <= _maxGap)
        {
            play.Entry.WatchedSeconds += (long)delta.TotalSeconds;
        }

        play.LastTickUtc = now;
    }

    private static void ApplyStream(PlaybackHistoryEntry entry, SessionInfo? session)
    {
        if (session is null)
        {
            return;
        }

        var method = session.PlayState?.PlayMethod;
        if (method.HasValue)
        {
            entry.PlayMethod = method.Value.ToString();
        }

        var transcoding = session.TranscodingInfo;
        if (transcoding is not null)
        {
            entry.VideoCodec = transcoding.VideoCodec ?? entry.VideoCodec;
            entry.AudioCodec = transcoding.AudioCodec ?? entry.AudioCodec;
            entry.Height = transcoding.Height ?? entry.Height;
            if (transcoding.TranscodeReasons != 0)
            {
                entry.TranscodeReasons = transcoding.TranscodeReasons.ToString();
            }
        }

        entry.RemoteAddress = session.RemoteEndPoint ?? entry.RemoteAddress;
    }

    private void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording playback statistics");
        }
    }

    private sealed class ActivePlay
    {
        public ActivePlay(PlaybackHistoryEntry entry) => Entry = entry;

        public PlaybackHistoryEntry Entry { get; }

        public bool IsPaused { get; set; }

        public DateTime LastTickUtc { get; set; }

        public DateTime LastSavedUtc { get; set; }
    }
}
