using System;

namespace MediaBrowser.Model.PlaybackStats;

/// <summary>
/// One recorded playback (from start to stop).
/// </summary>
public class PlaybackHistoryEntry
{
    /// <summary>
    /// Gets or sets the row id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the user name at the time of the playback.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name (episode or movie title).
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the item type (Episode, Movie, Audio, TvChannel...).
    /// </summary>
    public string? ItemType { get; set; }

    /// <summary>
    /// Gets or sets the series id for episodes.
    /// </summary>
    public Guid? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the series name for episodes.
    /// </summary>
    public string? SeriesName { get; set; }

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int? ParentIndexNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    public int? IndexNumber { get; set; }

    /// <summary>
    /// Gets or sets the production year.
    /// </summary>
    public int? ProductionYear { get; set; }

    /// <summary>
    /// Gets or sets the item runtime in ticks.
    /// </summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets when the playback started (UTC).
    /// </summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the playback stopped (UTC), null while playing.
    /// </summary>
    public DateTime? StoppedUtc { get; set; }

    /// <summary>
    /// Gets or sets the seconds actually watched (pauses excluded).
    /// </summary>
    public long WatchedSeconds { get; set; }

    /// <summary>
    /// Gets or sets the last known position in ticks.
    /// </summary>
    public long? PositionTicks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the item was watched to the end (or 90%).
    /// </summary>
    public bool Completed { get; set; }

    /// <summary>
    /// Gets or sets the client application.
    /// </summary>
    public string? Client { get; set; }

    /// <summary>
    /// Gets or sets the device name.
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Gets or sets the remote address.
    /// </summary>
    public string? RemoteAddress { get; set; }

    /// <summary>
    /// Gets or sets the play method (DirectPlay, DirectStream, Transcode).
    /// </summary>
    public string? PlayMethod { get; set; }

    /// <summary>
    /// Gets or sets the transcode reasons, if any.
    /// </summary>
    public string? TranscodeReasons { get; set; }

    /// <summary>
    /// Gets or sets the delivered video codec.
    /// </summary>
    public string? VideoCodec { get; set; }

    /// <summary>
    /// Gets or sets the delivered audio codec.
    /// </summary>
    public string? AudioCodec { get; set; }

    /// <summary>
    /// Gets or sets the delivered height (resolution).
    /// </summary>
    public int? Height { get; set; }
}
