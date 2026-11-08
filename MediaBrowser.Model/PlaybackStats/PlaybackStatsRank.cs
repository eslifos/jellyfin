using System;

namespace MediaBrowser.Model.PlaybackStats;

/// <summary>
/// One row of a ranking (user, series, movie, client...).
/// </summary>
public class PlaybackStatsRank
{
    /// <summary>
    /// Gets or sets the id (user, series or item id), when it applies.
    /// </summary>
    public Guid? Id { get; set; }

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the number of plays.
    /// </summary>
    public int Plays { get; set; }

    /// <summary>
    /// Gets or sets the seconds watched.
    /// </summary>
    public long WatchedSeconds { get; set; }

    /// <summary>
    /// Gets or sets the number of distinct users (for series / movies).
    /// </summary>
    public int Users { get; set; }

    /// <summary>
    /// Gets or sets the last time it was played (UTC).
    /// </summary>
    public DateTime? LastPlayedUtc { get; set; }
}
