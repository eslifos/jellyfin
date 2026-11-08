using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.PlaybackStats;

/// <summary>
/// Playback summary of one user.
/// </summary>
public class PlaybackUserStats
{
    /// <summary>
    /// Gets or sets the user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the user name of the last play.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets all time plays.
    /// </summary>
    public int Plays { get; set; }

    /// <summary>
    /// Gets or sets all time seconds watched.
    /// </summary>
    public long WatchedSeconds { get; set; }

    /// <summary>
    /// Gets or sets the plays of the last 30 days.
    /// </summary>
    public int Plays30 { get; set; }

    /// <summary>
    /// Gets or sets the seconds watched in the last 30 days.
    /// </summary>
    public long WatchedSeconds30 { get; set; }

    /// <summary>
    /// Gets or sets the first recorded play (UTC).
    /// </summary>
    public DateTime? FirstPlayUtc { get; set; }

    /// <summary>
    /// Gets or sets the last recorded play (UTC).
    /// </summary>
    public DateTime? LastPlayUtc { get; set; }

    /// <summary>
    /// Gets or sets the last play.
    /// </summary>
    public PlaybackHistoryEntry? LastPlay { get; set; }

    /// <summary>
    /// Gets or sets the seconds watched on each of the last 30 days (oldest first).
    /// </summary>
    public IReadOnlyList<long> Daily30 { get; set; } = [];

    /// <summary>
    /// Gets or sets the client used the most.
    /// </summary>
    public string? TopClient { get; set; }
}
