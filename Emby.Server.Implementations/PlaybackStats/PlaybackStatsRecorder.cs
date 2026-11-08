using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.PlaybackStats;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

    private readonly ISessionManager _sessionManager;
    private readonly PlaybackStatsStore _store;
    private readonly ILogger<PlaybackStatsRecorder> _logger;
    private readonly ConcurrentDictionary<string, ActivePlay> _active = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStatsRecorder"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="store">The store.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackStatsRecorder(ISessionManager sessionManager, PlaybackStatsStore store, ILogger<PlaybackStatsRecorder> logger)
    {
        _sessionManager = sessionManager;
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
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
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

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
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
        entry.Id = _store.Insert(entry);

        var play = new ActivePlay(entry) { IsPaused = e.IsPaused, LastTickUtc = entry.StartedUtc, LastSavedUtc = entry.StartedUtc };
        _active[key] = play;
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
