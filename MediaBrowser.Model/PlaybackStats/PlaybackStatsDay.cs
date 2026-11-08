namespace MediaBrowser.Model.PlaybackStats;

/// <summary>
/// Watch time of one day, split by kind.
/// </summary>
public class PlaybackStatsDay
{
    /// <summary>
    /// Gets or sets the local date (yyyy-MM-dd).
    /// </summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the seconds of episodes.
    /// </summary>
    public long EpisodeSeconds { get; set; }

    /// <summary>
    /// Gets or sets the seconds of movies.
    /// </summary>
    public long MovieSeconds { get; set; }

    /// <summary>
    /// Gets or sets the seconds of everything else (music, live TV...).
    /// </summary>
    public long OtherSeconds { get; set; }

    /// <summary>
    /// Gets or sets the number of plays.
    /// </summary>
    public int Plays { get; set; }
}
